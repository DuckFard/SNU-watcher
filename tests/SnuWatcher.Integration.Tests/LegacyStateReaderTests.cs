using SnuWatcher.Application.Migration;
using SnuWatcher.Infrastructure.Legacy;
using Xunit;

namespace SnuWatcher.Integration.Tests.Legacy;

public sealed class LegacyStateReaderTests
{
    [Fact]
    public async Task ReadsHistoricalFactsWithoutChangingTheDatabaseOrImportingAcknowledgements()
    {
        using var fixture = new LegacyFixture();
        using (var connection = fixture.CreateDatabase()) LegacyFixture.Insert(connection);
        var before = LegacyFixture.Hash(fixture.DatabasePath);

        var snapshot = await new LegacyWatchReader().ReadAsync(new(statePath: fixture.DatabasePath), default);

        Assert.Empty(snapshot.Diagnostics);
        var definition = Assert.Single(snapshot.Definitions);
        Assert.False(definition.WasConfigured);
        Assert.Equal("012", definition.Identity.Section);
        Assert.Equal(LegacyAvailability.Available, definition.HistoricalState!.Availability);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.Zero), definition.HistoricalState.ObservedAtUtc);
        Assert.Equal(before, LegacyFixture.Hash(fixture.DatabasePath));
    }

    [Fact]
    public async Task AQuiescentWalDatabaseKeepsMainAndWalBytesUnchanged()
    {
        using var fixture = new LegacyFixture();
        using var writer = fixture.CreateDatabase(wal: true);
        LegacyFixture.Insert(writer);
        var databaseBefore = LegacyFixture.Hash(fixture.DatabasePath);
        var walBefore = LegacyFixture.Hash(fixture.DatabasePath + "-wal");

        var snapshot = await new LegacyWatchReader().ReadAsync(new(statePath: fixture.DatabasePath), default);

        Assert.Single(snapshot.Definitions);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(databaseBefore, LegacyFixture.Hash(fixture.DatabasePath));
        Assert.Equal(walBefore, LegacyFixture.Hash(fixture.DatabasePath + "-wal"));
    }

    [Fact]
    public async Task MissingWalReadPrerequisitesFailWithoutCreatingSidecars()
    {
        using var fixture = new LegacyFixture();
        using (var connection = fixture.CreateDatabase(wal: true)) LegacyFixture.Insert(connection);
        Assert.False(File.Exists(fixture.DatabasePath + "-wal"));
        Assert.False(File.Exists(fixture.DatabasePath + "-shm"));
        var before = LegacyFixture.Hash(fixture.DatabasePath);
        var nativeReadStarted = false;
        var snapshot = await new LegacyWatchReader(() => nativeReadStarted = true)
            .ReadAsync(new(statePath: fixture.DatabasePath), default);
        Assert.Empty(snapshot.Definitions);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_database_wal_unavailable");
        Assert.False(nativeReadStarted);
        Assert.False(File.Exists(fixture.DatabasePath + "-wal"));
        Assert.False(File.Exists(fixture.DatabasePath + "-shm"));
        Assert.Equal(before, LegacyFixture.Hash(fixture.DatabasePath));
    }

    [Fact]
    public async Task ClosingTheLastWriterBetweenPrerequisitesAndOpenCannotRecreateSidecars()
    {
        using var fixture = new LegacyFixture();
        using var writer = fixture.CreateDatabase(wal: true);
        LegacyFixture.Insert(writer);
        var writerClosed = false;
        var snapshot = await new LegacyWatchReader(() => { }, () =>
        {
            writer.Dispose();
            writerClosed = true;
            Assert.True(File.Exists(fixture.DatabasePath + "-wal"));
            Assert.True(File.Exists(fixture.DatabasePath + "-shm"));
        }).ReadAsync(new(statePath: fixture.DatabasePath), default);
        Assert.True(writerClosed);
        Assert.Single(snapshot.Definitions);
        Assert.Empty(snapshot.Diagnostics);
    }

    [Fact]
    public async Task ConcurrentWalCommitCannotMixRowsFromDifferentSnapshots()
    {
        using var fixture = new LegacyFixture();
        using var keeper = fixture.CreateDatabase(wal: true);
        LegacyFixture.Insert(keeper, "001", 119);
        LegacyFixture.Insert(keeper, "002", 118);
        var hookRan = false;
        var reader = new LegacyWatchReader(() => Task.Run(() =>
        {
            using var writer = fixture.Factory.Open();
            LegacyFixture.Execute(writer, "UPDATE course_state SET registered=10;");
            LegacyFixture.Insert(writer, "003", 117);
            hookRan = true;
        }).GetAwaiter().GetResult());

        var snapshot = await reader.ReadAsync(new(statePath: fixture.DatabasePath), default);

        Assert.True(hookRan);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(new int?[] { 119, 118 }, snapshot.Definitions.Select(d => d.HistoricalState!.Registered));
        using var count = keeper.CreateCommand();
        count.CommandText = "SELECT count(*) FROM course_state;";
        Assert.Equal(3L, count.ExecuteScalar());
    }

    [Fact]
    public async Task CancellationAfterSnapshotAnchoringDoesNotChangeTheSource()
    {
        using var fixture = new LegacyFixture();
        using (var connection = fixture.CreateDatabase()) LegacyFixture.Insert(connection);
        var before = LegacyFixture.Hash(fixture.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        var reader = new LegacyWatchReader(cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new(statePath: fixture.DatabasePath), cancellation.Token));
        Assert.Equal(before, LegacyFixture.Hash(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("PRAGMA user_version=5;")]
    [InlineData("PRAGMA application_id=123;")]
    [InlineData("CREATE TABLE unrelated (value TEXT);")]
    [InlineData("CREATE TABLE sqliteXextra (value TEXT);")]
    [InlineData("ALTER TABLE course_state ADD COLUMN future TEXT;")]
    [InlineData("ALTER TABLE course_state RENAME COLUMN capacity TO quota;")]
    [InlineData("DROP TABLE course_state; CREATE VIEW course_state AS SELECT 1 AS year;")]
    public async Task FutureForeignOrDifferentSchemasFailWithoutResettingTheSource(string modification)
    {
        using var fixture = new LegacyFixture();
        using (var connection = fixture.CreateDatabase()) LegacyFixture.Execute(connection, modification);
        var before = LegacyFixture.Hash(fixture.DatabasePath);
        var snapshot = await new LegacyWatchReader().ReadAsync(new(statePath: fixture.DatabasePath), default);
        Assert.Empty(snapshot.Definitions);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_database_schema_unsupported");
        Assert.Equal(before, LegacyFixture.Hash(fixture.DatabasePath));
    }

    [Fact]
    public async Task IncoherentStorageKeysAreExcludedAndNaiveTimesKeepTheirOriginalZoneUnknown()
    {
        using var fixture = new LegacyFixture();
        using (var connection = fixture.CreateDatabase())
        {
            LegacyFixture.Insert(connection, "001", storageKey: "2026:U000200002:DIFFERENT:001");
            LegacyFixture.Insert(connection, "002", observed: "2026-11-01T10:30:00");
        }
        var snapshot = await new LegacyWatchReader().ReadAsync(new(statePath: fixture.DatabasePath), default);
        var valid = Assert.Single(snapshot.Definitions);
        Assert.Equal("002", valid.Identity.Section);
        Assert.Null(valid.HistoricalState!.ObservedAtUtc);
        Assert.Equal("2026-11-01T10:30:00", valid.HistoricalState.OriginalObservedText);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_state_row_invalid");
    }

    [Fact]
    public async Task BadHistoricalCountsDoNotDiscardAnOtherwiseValidDefinition()
    {
        using var fixture = new LegacyFixture();
        using (var connection = fixture.CreateDatabase()) LegacyFixture.Insert(connection, registered: -1);
        var snapshot = await new LegacyWatchReader().ReadAsync(new(statePath: fixture.DatabasePath), default);
        Assert.Null(Assert.Single(snapshot.Definitions).HistoricalState!.Registered);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_state_history_incomplete");
    }

    [Fact]
    public async Task OversizedHistoricalTextIsExcludedBeforeItCanBecomePreviewContent()
    {
        using var fixture = new LegacyFixture();
        using (var connection = fixture.CreateDatabase())
        {
            LegacyFixture.Insert(connection);
            LegacyFixture.Execute(connection, "UPDATE course_state SET name='short'||char(0)||replace(hex(zeroblob(50000)),'0','x');");
        }
        var snapshot = await new LegacyWatchReader().ReadAsync(new(statePath: fixture.DatabasePath), default);
        var definition = Assert.Single(snapshot.Definitions);
        Assert.Equal("F32.102L(012)", definition.Label);
        Assert.Null(definition.HistoricalState);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_state_history_incomplete");
    }

    [Fact]
    public async Task OversizedStateCollectionsFailRatherThanSilentlyImportingAPartialSet()
    {
        using var fixture = new LegacyFixture();
        using (var connection = fixture.CreateDatabase())
        {
            LegacyFixture.Execute(connection, """
                WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM n WHERE value<2001)
                INSERT INTO course_state SELECT '2026:U000200002:COURSE.'||value||':001',2026,'U000200002',
                    'COURSE.'||value,'001','full',0,0,'History',120,120,'2026-11-01T00:00:00Z' FROM n;
                """);
        }
        var snapshot = await new LegacyWatchReader().ReadAsync(new(statePath: fixture.DatabasePath), default);
        Assert.Empty(snapshot.Definitions);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_state_rows_exceeded");
    }
}
