using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreLegacyMigrationTests
{
    [Fact]
    public async Task GenuineV1UpgradePreservesPendingWorkAndBacksUpCommittedWalData()
    {
        using var fixture = new StoreLegacyV1Fixture();
        var before = fixture.LogicalSnapshot();

        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);

        Assert.Equal(5, fixture.Version);
        await AssertSeedPreservedAsync(store);
        var backup = Assert.Single(fixture.Backups());
        StoreLegacyV1Fixture.AssertBackupHeader(backup);
        using var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(before, StoreLegacyV1Fixture.LogicalSnapshot(saved));
        Assert.Equal(8, (await store.IssueCheckAsync(new CheckIssue(DateTimeOffset.UtcNow, [StoreLegacyV1Fixture.Key]), default)).Token.Ordinal);
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.MigrationBackupStarted, false)]
    [InlineData(SqliteStoreWriteStage.MigrationDdl, true)]
    [InlineData(SqliteStoreWriteStage.MigrationVersion, true)]
    public async Task InterruptedUpgradePreservesOriginalV1AndRetainsAnyCompletedBackup(SqliteStoreWriteStage target, bool hasBackup)
    {
        using var fixture = new StoreLegacyV1Fixture();
        var before = fixture.LogicalSnapshot();

        await Assert.ThrowsAsync<InjectedMigrationFailure>(() => SqliteWatchStore.OpenAsync(fixture.Factory,
            faultInjector: stage => { if (stage == target) throw new InjectedMigrationFailure(); }));

        Assert.Equal(1, fixture.Version);
        Assert.Equal(before, fixture.LogicalSnapshot());
        if (hasBackup)
        {
            var backup = Assert.Single(fixture.Backups());
            StoreLegacyV1Fixture.AssertBackupHeader(backup);
            using var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly);
            Assert.Equal(before, StoreLegacyV1Fixture.LogicalSnapshot(saved));
        }
        else Assert.Empty(fixture.Backups());

        await using var retry = await SqliteWatchStore.OpenAsync(fixture.Factory);
        Assert.Equal(5, fixture.Version);
        await AssertSeedPreservedAsync(retry);
    }

    [Fact]
    public async Task CancellationAtThePreBackupBoundaryDoesNotChangeV1OrCreateABackup()
    {
        using var fixture = new StoreLegacyV1Fixture();
        var before = fixture.LogicalSnapshot();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteWatchStore.OpenAsync(fixture.Factory,
            faultInjector: stage => { if (stage == SqliteStoreWriteStage.MigrationBackupStarted) cancellation.Cancel(); },
            cancellationToken: cancellation.Token));

        Assert.Equal(1, fixture.Version);
        Assert.Equal(before, fixture.LogicalSnapshot());
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public async Task ConcurrentV1OpenRechecksAfterTheMigrationReservationAndCreatesOnlyOneBackup()
    {
        using var fixture = new StoreLegacyV1Fixture();
        using var releaseFirst = new ManualResetEventSlim();
        var firstBackedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondValidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = SqliteWatchStore.OpenAsync(fixture.Factory, faultInjector: stage =>
        {
            if (stage != SqliteStoreWriteStage.MigrationBackup) return;
            firstBackedUp.TrySetResult();
            if (!releaseFirst.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Migration fixture was not released.");
        });
        Task<SqliteWatchStore>? secondTask = null;
        SqliteWatchStore? first = null;
        SqliteWatchStore? second = null;
        try
        {
            await firstBackedUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondTask = SqliteWatchStore.OpenAsync(fixture.Factory, faultInjector: stage =>
            {
                if (stage == SqliteStoreWriteStage.SchemaValidated) secondValidated.TrySetResult();
            });
            try { await secondValidated.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { releaseFirst.Set(); }
            first = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
            second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(5, fixture.Version);
            Assert.Single(fixture.Backups());
            await AssertSeedPreservedAsync(first);
            await AssertSeedPreservedAsync(second);
        }
        finally
        {
            releaseFirst.Set();
            if (first is not null) await first.DisposeAsync();
            else if (firstTask.IsCompletedSuccessfully) await (await firstTask).DisposeAsync();
            if (second is not null) await second.DisposeAsync();
            else if (secondTask?.IsCompletedSuccessfully == true) await (await secondTask).DisposeAsync();
        }
    }

    [Theory]
    [InlineData(999, StoreLegacyV1Fixture.AppId)]
    [InlineData(1, 7)]
    public async Task FutureOrForeignHeaderIsRejectedWithoutBackupCreation(long version, long appId)
    {
        using var fixture = new StoreLegacyV1Fixture();
        fixture.SetHeader(version, appId);
        var before = fixture.LogicalSnapshot();
        var databaseBytes = fixture.DatabaseHash();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(fixture.Factory));

        Assert.Equal(before, fixture.LogicalSnapshot());
        Assert.Equal(databaseBytes, fixture.DatabaseHash());
        Assert.Empty(fixture.Backups());
    }

    [Theory]
    [InlineData("ALTER TABLE Outbox RENAME COLUMN attempts TO oldAttempts;")]
    [InlineData("DROP INDEX PendingOutbox; CREATE INDEX PendingOutbox ON Outbox(attempts);")]
    [InlineData("PRAGMA foreign_keys=OFF; ALTER TABLE IssuedScopes RENAME TO oldScopes; CREATE TABLE IssuedScopes(tokenId TEXT NOT NULL,sectionKey TEXT NOT NULL,PRIMARY KEY(tokenId,sectionKey)); INSERT INTO IssuedScopes SELECT * FROM oldScopes; DROP TABLE oldScopes;")]
    public async Task OwnedLookingButChangedV1LayoutIsRejectedBeforeBackupOrMigration(string alteration)
    {
        using var fixture = new StoreLegacyV1Fixture();
        using (var mutate = fixture.Factory.Open())
        using (var command = mutate.CreateCommand())
        {
            command.CommandText = alteration;
            command.ExecuteNonQuery();
        }
        var before = fixture.LogicalSnapshot();
        var beforeBytes = fixture.DatabaseHash();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(fixture.Factory));

        Assert.Equal(1, fixture.Version);
        Assert.Equal(before, fixture.LogicalSnapshot());
        Assert.Equal(beforeBytes, fixture.DatabaseHash());
        Assert.Empty(fixture.Backups());
    }

    private static async Task AssertSeedPreservedAsync(SqliteWatchStore store)
    {
        var watch = Assert.Single(await store.ListSubscriptionsAsync(default));
        Assert.Equal(StoreLegacyV1Fixture.SubscriptionId, watch.Id);
        Assert.Equal("Frozen V1 서울", watch.Label);
        Assert.Equal(5, watch.DefinitionRevision);
        Assert.Equal(2, watch.ActivationGeneration);
        Assert.Equal(7, Assert.Single(watch.Members).InitializedByAttemptOrdinal);
        var section = await store.ReadSectionAsync(StoreLegacyV1Fixture.Key, default);
        Assert.Equal(7, section.LatestCompletedOrdinal);
        Assert.Equal(1, section.Baseline!.Seats.ApparentSeats);
        Assert.Equal(4, section.Version);
        var pending = Assert.Single(await store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(StoreLegacyV1Fixture.EventId, pending.EventId);
        Assert.Equal(StoreLegacyV1Fixture.EventId.ToString("N"), pending.NativeDedupeKey);
        Assert.Equal(1, pending.Trigger.CurrentSeats);
        Assert.Equal(DeliveryStatus.Pending, pending.Status);
        Assert.Equal(0, pending.Attempts);
        Assert.Equal(StoreLegacyV1Fixture.SubscriptionId.ToString("N"), Assert.Single(pending.Trigger.Recipients).SubscriptionId);
    }

    private sealed class InjectedMigrationFailure : Exception;
}
