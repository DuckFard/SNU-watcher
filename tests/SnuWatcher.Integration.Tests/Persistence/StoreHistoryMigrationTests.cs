using Microsoft.Data.Sqlite;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreHistoryMigrationTests
{
    [Fact]
    public async Task GenuineV4WalUpgradeAddsOnlyHistoryIndexAndBacksUpOriginalPreferenceBytes()
    {
        using var fixture = new StoreHistoryV4Fixture();
        var before = fixture.LogicalSnapshot();
        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);
        Assert.Equal(5, fixture.Version);
        var backup = Assert.Single(fixture.Backups());
        Assert.StartsWith(Path.GetFileName(fixture.DatabasePath) + ".before-v5-", Path.GetFileName(backup), StringComparison.Ordinal);
        StoreBatchV2Fixture.AssertStandaloneBackup(backup, 4);
        using var saved = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(before, StoreHistoryV4Fixture.LogicalSnapshot(saved));
        using var upgraded = fixture.Factory.Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(StoreHistoryV4Fixture.ValuesJson, StoreBatchV2Fixture.Scalar(upgraded, "SELECT valuesJson FROM UiPreferences;"));
        Assert.Equal(StoreHistoryV4Fixture.CommandJson, StoreBatchV2Fixture.Scalar(upgraded, "SELECT commandFacts FROM UiPreferenceWrites;"));
        Assert.Equal(StoreHistoryV4Fixture.ReceiptJson, StoreBatchV2Fixture.Scalar(upgraded, "SELECT receipt FROM UiPreferenceWrites;"));
        Assert.Equal("CREATE INDEX AlertHistoryOrder ON Events(triggerOrdinal DESC,id COLLATE BINARY DESC)",
            StoreBatchV2Fixture.Scalar(upgraded, "SELECT sql FROM sqlite_schema WHERE name='AlertHistoryOrder';"));
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.MigrationBackupStarted, false)]
    [InlineData(SqliteStoreWriteStage.MigrationBackup, true)]
    [InlineData(SqliteStoreWriteStage.MigrationDdl, true)]
    [InlineData(SqliteStoreWriteStage.MigrationVersion, true)]
    public async Task InterruptedV4UpgradePreservesOriginalAndAnyCompletedVerifiedBackup(SqliteStoreWriteStage stage, bool complete)
    {
        using var fixture = new StoreHistoryV4Fixture();
        var original = fixture.LogicalSnapshot();
        await Assert.ThrowsAsync<InjectedFailure>(() => SqliteWatchStore.OpenAsync(fixture.Factory,
            faultInjector: at => { if (at == stage) throw new InjectedFailure(); }));
        Assert.Equal(4, fixture.Version);
        Assert.Equal(original, fixture.LogicalSnapshot());
        Assert.Equal(complete ? 1 : 0, fixture.Backups().Length);
        if (complete) StoreBatchV2Fixture.AssertStandaloneBackup(Assert.Single(fixture.Backups()), 4);
        await using var retry = await SqliteWatchStore.OpenAsync(fixture.Factory);
        Assert.Equal(5, fixture.Version);
    }

    [Theory]
    [InlineData("CREATE INDEX AlertHistoryOrder ON Events(triggerOrdinal,id);")]
    [InlineData("ALTER TABLE UiPreferences RENAME COLUMN valuesJson TO oldValues;")]
    [InlineData("PRAGMA user_version=999;")]
    [InlineData("PRAGMA application_id=12345;")]
    public async Task AlteredFutureOrForeignV4IsRejectedBeforeAnyMigrationOrJournalChange(string alteration)
    {
        using var fixture = new StoreHistoryV4Fixture();
        fixture.Alter(alteration);
        var original = fixture.LogicalSnapshot(); var bytes = fixture.DatabaseHash();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(fixture.Factory));
        Assert.Equal(original, fixture.LogicalSnapshot());
        Assert.Equal(bytes, fixture.DatabaseHash());
        Assert.Empty(fixture.Backups());
    }

    private sealed class InjectedFailure : Exception;
}
