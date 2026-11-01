using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreUiMigrationTests
{
    [Fact]
    public async Task GenuineV3WalUpgradePreservesAllDeliveryAndSourceFactsAndOneOriginalBackup()
    {
        using var fixture = new StoreUiV3Fixture();
        var original = fixture.LogicalSnapshot();
        var rows = fixture.HistoricalRows();

        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);

        Assert.Equal(4, fixture.Version);
        var backup = Assert.Single(fixture.Backups());
        Assert.StartsWith(Path.GetFileName(fixture.DatabasePath) + ".before-v4-", Path.GetFileName(backup), StringComparison.Ordinal);
        StoreBatchV2Fixture.AssertStandaloneBackup(backup, 3);
        using (var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly))
            Assert.Equal(original, StoreUiV3Fixture.LogicalSnapshot(saved));
        Assert.Equal(rows, fixture.HistoricalRows());
        var unresolved = Assert.Single(await ((IDeliveryRecoveryStore)store).ReadUnresolvedAttemptsAsync(null, 10, default));
        Assert.Equal(StoreUiV3Fixture.UnresolvedAttemptId, unresolved.AttemptId.ToString("N"));
        Assert.Equal(StoreUiV3Fixture.BatchId, unresolved.BatchId.ToString("N"));
        var policy = await ((ISourcePolicyStore)store).ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang, default);
        Assert.Equal(1, policy.AccessPause!.CausingOrdinal);
        Assert.Equal(2, policy.Revision);
        Assert.Equal(StoreBatchV2Fixture.SubscriptionId, Assert.Single(await store.ListSubscriptionsAsync()).Id);
        AssertUiTablesEmpty(fixture.Factory);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GenuineEarlierProducerUpgradesDirectlyToFourWithOneBackupOfItsOriginalVersion(int version)
    {
        using var one = version == 1 ? new StoreLegacyV1Fixture() : null;
        using var two = version == 2 ? new StoreBatchV2Fixture() : null;
        var factory = one?.Factory ?? two!.Factory;
        var path = one?.DatabasePath ?? two!.DatabasePath;
        var before = one?.LogicalSnapshot() ?? two!.LogicalSnapshot();

        await using var store = await SqliteWatchStore.OpenAsync(factory);

        using var upgraded = factory.Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(4L, StoreBatchV2Fixture.Scalar(upgraded, "PRAGMA user_version;"));
        var backup = Assert.Single(StoreBatchV2Fixture.AllBackups(path));
        Assert.Contains(".before-v4-", backup, StringComparison.Ordinal);
        StoreBatchV2Fixture.AssertStandaloneBackup(backup, version);
        using var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(before, version == 1 ? StoreLegacyV1Fixture.LogicalSnapshot(saved) : StoreBatchV2Fixture.LogicalSnapshot(saved));
        AssertUiTablesEmpty(factory);
        Assert.Single(await store.ListSubscriptionsAsync());
        Assert.Single(await store.ReadPendingDeliveriesAsync(10));
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.MigrationBackupStarted, false)]
    [InlineData(SqliteStoreWriteStage.MigrationBackup, true)]
    [InlineData(SqliteStoreWriteStage.MigrationDdl, true)]
    [InlineData(SqliteStoreWriteStage.MigrationVersion, true)]
    public async Task InterruptedV3UpgradeRollsBackAllFourTablesAndPreservesVerifiedBackup(SqliteStoreWriteStage stage, bool completedBackup)
    {
        using var fixture = new StoreUiV3Fixture();
        var original = fixture.LogicalSnapshot();

        await Assert.ThrowsAsync<InjectedFailure>(() => SqliteWatchStore.OpenAsync(fixture.Factory,
            faultInjector: at => { if (stage == at) throw new InjectedFailure(); }));

        Assert.Equal(3, fixture.Version);
        Assert.Equal(original, fixture.LogicalSnapshot());
        Assert.Equal(completedBackup ? 1 : 0, fixture.Backups().Length);
        if (completedBackup) StoreBatchV2Fixture.AssertStandaloneBackup(Assert.Single(fixture.Backups()), 3);
        await using var retry = await SqliteWatchStore.OpenAsync(fixture.Factory);
        Assert.Equal(4, fixture.Version);
    }

    [Theory]
    [InlineData("ALTER TABLE SourceRequests RENAME COLUMN resumeWatermark TO oldWatermark;")]
    [InlineData("DROP INDEX OpenDeliveryBatch; CREATE UNIQUE INDEX OpenDeliveryBatch ON DeliveryBatches(sectionKey) WHERE status=1;")]
    public async Task AlteredV3IsRejectedWithoutBackupOrAnyHeaderMutation(string alteration)
    {
        using var fixture = new StoreUiV3Fixture();
        fixture.Alter(alteration);
        var original = fixture.LogicalSnapshot();
        var bytes = fixture.DatabaseHash();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(fixture.Factory));

        Assert.Equal(original, fixture.LogicalSnapshot());
        Assert.Equal(bytes, fixture.DatabaseHash());
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public async Task FutureV3HeaderDoesNotCreateBackupOrMutateDatabase()
    {
        using var fixture = new StoreUiV3Fixture();
        fixture.SetHeader(999, StoreBatchV2Fixture.AppId);
        var before = fixture.LogicalSnapshot();
        var bytes = fixture.DatabaseHash();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(fixture.Factory));
        Assert.Equal(before, fixture.LogicalSnapshot());
        Assert.Equal(bytes, fixture.DatabaseHash());
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public async Task ConcurrentV3OpenRechecksReservationAndCreatesOnlyOneOriginalBackup()
    {
        using var fixture = new StoreUiV3Fixture();
        using var release = new ManualResetEventSlim();
        var backedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = SqliteWatchStore.OpenAsync(fixture.Factory, faultInjector: stage =>
        {
            if (stage != SqliteStoreWriteStage.MigrationBackup) return;
            backedUp.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The private migration fixture was not released.");
        });
        Task<SqliteWatchStore>? second = null;
        try
        {
            var completed = await Task.WhenAny(backedUp.Task, first).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(backedUp.Task, completed);
            second = SqliteWatchStore.OpenAsync(fixture.Factory, faultInjector: stage =>
            { if (stage == SqliteStoreWriteStage.SchemaValidated) inspected.TrySetResult(); });
            await inspected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); }
        await using var firstStore = await first.WaitAsync(TimeSpan.FromSeconds(10));
        await using var secondStore = await second!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, fixture.Version);
        StoreBatchV2Fixture.AssertStandaloneBackup(Assert.Single(fixture.Backups()), 3);
    }

    private static void AssertUiTablesEmpty(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open(SqliteOpenMode.ReadOnly);
        foreach (var table in new[] { "UiPreferences", "UiPreferenceWrites", "UiTermHints" })
            Assert.Equal(0L, StoreBatchV2Fixture.Scalar(connection, "SELECT count(*) FROM " + table + ";"));
        Assert.Equal(0L, StoreBatchV2Fixture.Scalar(connection, "SELECT revision FROM UiTermCacheState WHERE id=1;"));
    }
    private sealed class InjectedFailure : Exception;
}
