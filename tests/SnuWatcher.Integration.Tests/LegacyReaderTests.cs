using System.Text.Json;
using SnuWatcher.Application.Migration;
using SnuWatcher.Infrastructure.Legacy;
using Xunit;

namespace SnuWatcher.Integration.Tests.Legacy;

public sealed class LegacyReaderTests
{
    [Fact]
    public async Task ReadsOnlyConfiguredCoursesAndPreservesExactStringsWithoutFollowingOtherSettings()
    {
        using var fixture = new LegacyFixture();
        fixture.WriteConfig("[watcher]\nstate_database = \"never-created.sqlite3\"\nwebhook_url_environment_variable = \"NEVER_READ_THIS_ENVIRONMENT\"\n"
            + LegacyFixture.Course() + LegacyFixture.Course("001", "4190.412", "컴퓨터모델링"));
        var before = LegacyFixture.Hash(fixture.ConfigPath);

        var snapshot = await new LegacyWatchReader().ReadAsync(new(fixture.ConfigPath), default);

        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(2, snapshot.Definitions.Length);
        Assert.All(snapshot.Definitions, definition => { Assert.True(definition.WasConfigured); Assert.Null(definition.HistoricalState); });
        Assert.Equal("012", snapshot.Definitions[0].Identity.Section);
        Assert.Equal("F32.102L", snapshot.Definitions[0].Identity.CourseCode);
        Assert.Equal("통계학실험", snapshot.Definitions[0].Label);
        Assert.False(File.Exists(fixture.MissingPath));
        Assert.Equal(before, LegacyFixture.Hash(fixture.ConfigPath));
    }

    [Fact]
    public async Task InvalidTomlReturnsOnlyASafeCodeAndNumericPositions()
    {
        using var fixture = new LegacyFixture();
        fixture.WriteConfig("secret = \"DO_NOT_LOG_THIS\"\n[[courses]\nsection=\"012\"");
        var snapshot = await new LegacyWatchReader().ReadAsync(new(fixture.ConfigPath), default);
        var diagnostic = Assert.Single(snapshot.Diagnostics);
        Assert.Empty(snapshot.Definitions);
        Assert.Equal("legacy_config_syntax_invalid", diagnostic.Code);
        Assert.Equal(2, diagnostic.LineNumber);
        Assert.True(diagnostic.ColumnNumber > 0);
        var serialized = JsonSerializer.Serialize(snapshot.Diagnostics);
        Assert.DoesNotContain("DO_NOT_LOG_THIS", serialized);
        Assert.DoesNotContain(fixture.ConfigPath, serialized);
    }

    [Fact]
    public async Task DuplicateAndInvalidIdentityRowsDoNotSilentlyChangeTheSelection()
    {
        using var fixture = new LegacyFixture();
        fixture.WriteConfig(LegacyFixture.Course() + LegacyFixture.Course() + LegacyFixture.Course("١٢٣")
            + LegacyFixture.Course("123").Replace("year = 2026", "year = \"2026\"", StringComparison.Ordinal));
        var snapshot = await new LegacyWatchReader().ReadAsync(new(fixture.ConfigPath), default);
        Assert.Single(snapshot.Definitions);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_config_duplicate" && d.RowNumber == 2);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_config_row_invalid" && d.RowNumber == 3);
        Assert.Contains(snapshot.Diagnostics, d => d.Code == "legacy_config_row_invalid" && d.RowNumber == 4);
    }

    [Fact]
    public async Task EmptyOrOversizedCourseCollectionsFailExplicitly()
    {
        using var fixture = new LegacyFixture();
        fixture.WriteConfig("[watcher]\npoll_interval_seconds=60\n");
        var empty = await new LegacyWatchReader().ReadAsync(new(fixture.ConfigPath), default);
        Assert.Contains(empty.Diagnostics, d => d.Code == "legacy_config_courses_missing");
        fixture.WriteConfig(string.Concat(Enumerable.Range(0, 21).Select(i => LegacyFixture.Course(i.ToString("D3")))));
        var oversized = await new LegacyWatchReader().ReadAsync(new(fixture.ConfigPath), default);
        Assert.Empty(oversized.Definitions);
        Assert.Contains(oversized.Diagnostics, d => d.Code == "legacy_config_rows_exceeded");
    }

    [Fact]
    public async Task ConfigAndStateMergeByFourPartIdentityAndStateOnlyRowsRemainUnselected()
    {
        using var fixture = new LegacyFixture();
        fixture.WriteConfig(LegacyFixture.Course(label: "My selected section"));
        using (var connection = fixture.CreateDatabase())
        { LegacyFixture.Insert(connection); LegacyFixture.Insert(connection, "001", 118); }

        var snapshot = await new LegacyWatchReader().ReadAsync(new(fixture.ConfigPath, fixture.DatabasePath), default);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(2, snapshot.Definitions.Length);
        var configured = snapshot.Definitions.Single(d => d.WasConfigured);
        Assert.Equal("My selected section", configured.Label);
        Assert.Equal(119, configured.HistoricalState!.Registered);
        Assert.False(snapshot.Definitions.Single(d => d.Identity.Section == "001").WasConfigured);
    }

    [Fact]
    public async Task MissingSelectedFilesReturnDiagnosticsWithoutCreatingThem()
    {
        using var fixture = new LegacyFixture();
        var snapshot = await new LegacyWatchReader().ReadAsync(new(fixture.ConfigPath, fixture.DatabasePath), default);
        Assert.Empty(snapshot.Definitions);
        Assert.Equal(2, snapshot.Diagnostics.Length);
        Assert.All(snapshot.Diagnostics, d => Assert.Equal("legacy_source_missing", d.Code));
        Assert.False(File.Exists(fixture.ConfigPath));
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public async Task CanceledReadPropagatesCancellationWithoutReadingTheSource()
    {
        using var fixture = new LegacyFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LegacyWatchReader()
            .ReadAsync(new(fixture.ConfigPath), cancellation.Token));
        Assert.False(File.Exists(fixture.ConfigPath));
    }
}
