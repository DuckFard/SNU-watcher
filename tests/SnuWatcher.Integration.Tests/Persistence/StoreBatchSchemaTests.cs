using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreBatchSchemaTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FrozenV1UpgradesDirectlyToV5WithOneOriginalVersionBackupAndNoFabricatedWork()
    {
        using var fixture = new StoreLegacyV1Fixture();
        var original = fixture.LogicalSnapshot();
        string historicalRows;
        using (var before = fixture.Factory.Open(SqliteOpenMode.ReadOnly))
            historicalRows = StoreBatchV2Fixture.HistoricalRows(before, includeLegacy: false);

        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);

        Assert.Equal(5, fixture.Version);
        await AssertPendingSeedAsync(store, "Frozen V1 서울");
        var backup = AssertOriginalVersionBackup(fixture.DatabasePath, 1);
        using (var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly))
            Assert.Equal(original, StoreLegacyV1Fixture.LogicalSnapshot(saved));
        using var upgraded = fixture.Factory.Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(historicalRows, StoreBatchV2Fixture.HistoricalRows(upgraded, includeLegacy: false));
        Assert.Equal(0L, StoreBatchV2Fixture.Scalar(upgraded, "SELECT count(*) FROM LegacyImports;"));
        AssertEmptyV3Additions(upgraded);
    }

    [Fact]
    public async Task FrozenV2WalUpgradePreservesPendingEventAndImportLedgerAndBacksUpOriginalV2()
    {
        using var fixture = new StoreBatchV2Fixture();
        var original = fixture.LogicalSnapshot();
        var historicalRows = fixture.HistoricalRows();

        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);

        Assert.Equal(5, fixture.Version);
        await AssertPendingSeedAsync(store, "Frozen V2 서울");
        var backup = AssertOriginalVersionBackup(fixture.DatabasePath, 2);
        using (var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly))
            Assert.Equal(original, StoreBatchV2Fixture.LogicalSnapshot(saved));
        using (var upgraded = fixture.Factory.Open(SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(historicalRows, StoreBatchV2Fixture.HistoricalRows(upgraded));
            Assert.Equal(StoreBatchV2Fixture.Provenance, StoreBatchV2Fixture.Scalar(upgraded, "SELECT provenance FROM LegacyImports;"));
            AssertEmptyV3Additions(upgraded);
        }

        var creation = new NewSubscription(Guid.NewGuid(), "Changed reimport label", SubscriptionKind.Section,
            [StoreBatchV2Fixture.Key], false, null, DateTimeOffset.UtcNow);
        var item = new LegacyImportItem(new LegacyCourseIdentity(2026, "U000200002", "F32.102", "012"),
            StoreBatchV2Fixture.Key, creation);
        var replay = Assert.Single((await store.ImportLegacyBatchAsync(new LegacyImportBatch([item]), default)).Items);
        Assert.False(replay.Created);
        Assert.Equal(StoreBatchV2Fixture.SubscriptionId, replay.Subscription.Id);
        Assert.Equal("Frozen V2 서울", replay.Subscription.Label);
        Assert.Equal(5, replay.Subscription.DefinitionRevision);
        Assert.Single(await store.ListSubscriptionsAsync(default));
        Assert.Equal(8, (await store.IssueCheckAsync(new CheckIssue(DateTimeOffset.UtcNow, [StoreBatchV2Fixture.Key]), default)).Token.Ordinal);
    }

    [Fact]
    public async Task EmptyDatabaseCreatesV5DirectlyWithoutBackupOrDeliveryWork()
    {
        using var fixture = new StoreBatchV2Fixture(createHistoricalSchema: false);

        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);

        Assert.Equal(5, fixture.Version);
        Assert.Empty(fixture.Backups());
        Assert.Empty(await store.ListSubscriptionsAsync(default));
        Assert.Empty(await store.ReadPendingDeliveriesAsync(20, default));
        using var verify = fixture.Factory.Open(SqliteOpenMode.ReadOnly);
        AssertEmptyV3Additions(verify);
        Assert.Equal(StoreBatchV2Fixture.AppId, StoreBatchV2Fixture.Scalar(verify, "PRAGMA application_id;"));
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.MigrationBackupStarted, false)]
    [InlineData(SqliteStoreWriteStage.MigrationBackup, true)]
    [InlineData(SqliteStoreWriteStage.MigrationDdl, true)]
    [InlineData(SqliteStoreWriteStage.MigrationVersion, true)]
    public async Task InterruptedV2UpgradeRollsBackEverythingAndRetryKeepsCompletedOriginalBackups(
        SqliteStoreWriteStage failureStage, bool completedBackup)
    {
        using var fixture = new StoreBatchV2Fixture();
        var original = fixture.LogicalSnapshot();

        await Assert.ThrowsAsync<InjectedV3Failure>(() => SqliteWatchStore.OpenAsync(fixture.Factory,
            faultInjector: stage => { if (stage == failureStage) throw new InjectedV3Failure(); }));

        Assert.Equal(2, fixture.Version);
        Assert.Equal(original, fixture.LogicalSnapshot());
        if (completedBackup)
        {
            var backup = AssertOriginalVersionBackup(fixture.DatabasePath, 2);
            using var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly);
            Assert.Equal(original, StoreBatchV2Fixture.LogicalSnapshot(saved));
        }
        else Assert.Empty(fixture.Backups());

        var retainedBackups = fixture.Backups();
        await using var retry = await SqliteWatchStore.OpenAsync(fixture.Factory);
        Assert.Equal(5, fixture.Version);
        await AssertPendingSeedAsync(retry, "Frozen V2 서울");
        Assert.Equal(completedBackup ? 2 : 1, fixture.Backups().Length);
        Assert.All(retainedBackups, path => Assert.Contains(path, fixture.Backups()));
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.MigrationDdl)]
    [InlineData(SqliteStoreWriteStage.MigrationVersion)]
    public async Task InterruptedDirectV1UpgradeDoesNotCommitAnIntermediateV2(SqliteStoreWriteStage failureStage)
    {
        using var fixture = new StoreLegacyV1Fixture();
        var original = fixture.LogicalSnapshot();

        await Assert.ThrowsAsync<InjectedV3Failure>(() => SqliteWatchStore.OpenAsync(fixture.Factory,
            faultInjector: stage => { if (stage == failureStage) throw new InjectedV3Failure(); }));

        Assert.Equal(1, fixture.Version);
        Assert.Equal(original, fixture.LogicalSnapshot());
        var backup = AssertOriginalVersionBackup(fixture.DatabasePath, 1);
        using (var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly))
            Assert.Equal(original, StoreLegacyV1Fixture.LogicalSnapshot(saved));

        await using var retry = await SqliteWatchStore.OpenAsync(fixture.Factory);
        Assert.Equal(5, fixture.Version);
        await AssertPendingSeedAsync(retry, "Frozen V1 서울");
        Assert.Equal(2, StoreBatchV2Fixture.AllBackups(fixture.DatabasePath).Length);
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.MigrationBackupStarted, false)]
    [InlineData(SqliteStoreWriteStage.MigrationDdl, true)]
    public async Task CancellationAtV2MigrationBoundaryKeepsOriginalVersionAndRows(
        SqliteStoreWriteStage cancellationStage, bool completedBackup)
    {
        using var fixture = new StoreBatchV2Fixture();
        var original = fixture.LogicalSnapshot();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteWatchStore.OpenAsync(fixture.Factory,
            faultInjector: stage => { if (stage == cancellationStage) cancellation.Cancel(); },
            cancellationToken: cancellation.Token));

        Assert.Equal(2, fixture.Version);
        Assert.Equal(original, fixture.LogicalSnapshot());
        if (completedBackup) AssertOriginalVersionBackup(fixture.DatabasePath, 2);
        else Assert.Empty(fixture.Backups());
    }

    [Fact]
    public async Task ConcurrentV2OpenCreatesOneV3UpgradeAndOnlyOneOriginalVersionBackup()
    {
        using var fixture = new StoreBatchV2Fixture();
        using var releaseFirst = new ManualResetEventSlim();
        var firstBackedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondValidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = SqliteWatchStore.OpenAsync(fixture.Factory, faultInjector: stage =>
        {
            if (stage != SqliteStoreWriteStage.MigrationBackup) return;
            firstBackedUp.TrySetResult();
            if (!releaseFirst.Wait(Timeout)) throw new TimeoutException("The V3 migration fixture was not released.");
        });
        Task<SqliteWatchStore>? secondTask = null;
        SqliteWatchStore? first = null;
        SqliteWatchStore? second = null;
        try
        {
            await AssertBoundaryReachedAsync(firstBackedUp.Task, firstTask);
            secondTask = SqliteWatchStore.OpenAsync(fixture.Factory, faultInjector: stage =>
            {
                if (stage == SqliteStoreWriteStage.SchemaValidated) secondValidated.TrySetResult();
            });
            try { await AssertBoundaryReachedAsync(secondValidated.Task, secondTask); }
            finally { releaseFirst.Set(); }
            first = await firstTask.WaitAsync(Timeout);
            second = await secondTask.WaitAsync(Timeout);

            Assert.Equal(5, fixture.Version);
            AssertOriginalVersionBackup(fixture.DatabasePath, 2);
            await AssertPendingSeedAsync(first, "Frozen V2 서울");
            await AssertPendingSeedAsync(second, "Frozen V2 서울");
        }
        finally
        {
            releaseFirst.Set();
            if (first is not null) await first.DisposeAsync();
            else if (!firstTask.IsFaulted && !firstTask.IsCanceled) await (await firstTask.WaitAsync(Timeout)).DisposeAsync();
            if (second is not null) await second.DisposeAsync();
            else if (secondTask is not null && !secondTask.IsFaulted && !secondTask.IsCanceled)
                await (await secondTask.WaitAsync(Timeout)).DisposeAsync();
        }
    }

    [Theory]
    [InlineData(999, StoreBatchV2Fixture.AppId)]
    [InlineData(2, 7)]
    public async Task FutureOrForeignV2HeaderIsRejectedWithoutBackupOrMutation(long version, long applicationId)
    {
        using var fixture = new StoreBatchV2Fixture();
        fixture.SetHeader(version, applicationId);
        var original = fixture.LogicalSnapshot();
        var bytes = fixture.DatabaseHash();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(fixture.Factory));

        Assert.Equal(original, fixture.LogicalSnapshot());
        Assert.Equal(bytes, fixture.DatabaseHash());
        Assert.Empty(fixture.Backups());
    }

    [Theory]
    [InlineData("ALTER TABLE LegacyImports RENAME COLUMN provenance TO historicalProvenance;")]
    [InlineData("DROP INDEX PendingOutbox; CREATE INDEX PendingOutbox ON Outbox(attempts);")]
    [InlineData("PRAGMA foreign_keys=OFF; ALTER TABLE LegacyImports RENAME TO oldLegacyImports; CREATE TABLE LegacyImports(legacyKey TEXT NOT NULL,targetKey TEXT NOT NULL,subscriptionId TEXT NOT NULL,importedUtc TEXT NOT NULL,provenance TEXT,PRIMARY KEY(legacyKey,targetKey)); INSERT INTO LegacyImports SELECT * FROM oldLegacyImports; DROP TABLE oldLegacyImports;")]
    public async Task AlteredOwnedLookingV2LayoutIsRejectedBeforeBackupOrDdl(string alteration)
    {
        using var fixture = new StoreBatchV2Fixture();
        fixture.Alter(alteration);
        var original = fixture.LogicalSnapshot();
        var bytes = fixture.DatabaseHash();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(fixture.Factory));

        Assert.Equal(2, fixture.Version);
        Assert.Equal(original, fixture.LogicalSnapshot());
        Assert.Equal(bytes, fixture.DatabaseHash());
        Assert.Empty(fixture.Backups());
    }

    private static string AssertOriginalVersionBackup(string databasePath, long originalVersion)
    {
        var backup = Assert.Single(StoreBatchV2Fixture.AllBackups(databasePath));
        Assert.StartsWith(Path.GetFileName(databasePath) + ".before-v5-", Path.GetFileName(backup), StringComparison.Ordinal);
        var prefixLength = Path.GetFileName(databasePath).Length + ".before-v5-".Length;
        Assert.True(Guid.TryParseExact(Path.GetFileName(backup)[prefixLength..^".sqlite3".Length], "N", out _));
        StoreBatchV2Fixture.AssertStandaloneBackup(backup, originalVersion);
        return backup;
    }

    private static void AssertEmptyV3Additions(SqliteConnection connection)
    {
        foreach (var table in new[] { "DeliveryBatches", "DeliveryBatchEvents", "DeliveryBatchAttempts", "DeliveryBatchAttemptEvents",
            "DeliveryBatchResults", "SourcePolicies", "SourceRequests", "SourceRequestResults" })
            Assert.Equal(0L, StoreBatchV2Fixture.Scalar(connection, "SELECT count(*) FROM " + table + ";"));
    }

    private static async Task AssertPendingSeedAsync(SqliteWatchStore store, string label)
    {
        var watch = Assert.Single(await store.ListSubscriptionsAsync(default));
        Assert.Equal(StoreBatchV2Fixture.SubscriptionId, watch.Id);
        Assert.Equal(label, watch.Label);
        Assert.Equal(5, watch.DefinitionRevision);
        Assert.Equal(2, watch.ActivationGeneration);
        Assert.Equal(7, Assert.Single(watch.Members).InitializedByAttemptOrdinal);
        var section = await store.ReadSectionAsync(StoreBatchV2Fixture.Key, default);
        Assert.Equal(7, section.LatestCompletedOrdinal);
        Assert.Equal(1, section.Baseline!.Seats.ApparentSeats);
        Assert.Equal(4, section.Version);
        var pending = Assert.Single(await store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(StoreBatchV2Fixture.EventId, pending.EventId);
        Assert.Equal(StoreBatchV2Fixture.EventId.ToString("N"), pending.NativeDedupeKey);
        Assert.Equal(1, pending.Trigger.CurrentSeats);
        Assert.Equal(DeliveryStatus.Pending, pending.Status);
        Assert.Equal(0, pending.Attempts);
        Assert.Equal(StoreBatchV2Fixture.SubscriptionId.ToString("N"), Assert.Single(pending.Trigger.Recipients).SubscriptionId);
    }

    private static async Task AssertBoundaryReachedAsync(Task boundary, Task<SqliteWatchStore> opening)
    {
        // Fail RED promptly when the old initializer returns without performing the new migration.
        var completed = await Task.WhenAny(boundary, opening).WaitAsync(Timeout);
        if (completed == opening) await opening;
        Assert.True(boundary.IsCompletedSuccessfully, "The opener returned without reaching the required migration boundary.");
        await boundary.WaitAsync(Timeout);
    }

    private sealed class InjectedV3Failure : Exception;
}
