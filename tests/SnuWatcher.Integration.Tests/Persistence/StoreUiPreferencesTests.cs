using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreUiPreferencesTests
{
    private static IUiPreferencesStore Preferences(StoreFixture fixture) => (IUiPreferencesStore)(object)fixture.Store;

    [Fact]
    public async Task MissingPreferencesAreEnglishDefaultsWithoutWritingAnyRowsOrWatchFacts()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var snapshot = await Preferences(fixture).ReadAsync();
        Assert.Equal(0, snapshot.Revision);
        Assert.Equal(UiLanguage.English, snapshot.Values.Language);
        Assert.Equal(WatchMonitorProfile.Normal, snapshot.Values.DesiredProfile);
        Assert.Null(snapshot.SavedAtUtc);
        Assert.Equal(0L, Scalar(fixture, "SELECT count(*) FROM UiPreferences;"));
        Assert.Equal(0L, Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
        Assert.Equal(0L, Scalar(fixture, "SELECT issuedOrdinal+catalogRevision FROM StoreMetadata;"));
    }

    [Fact]
    public async Task MutedSoundRoundTripsAcrossReopenAsAnExplicitBooleanWithoutLosingOtherFields()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        Assert.Equal(5L, Scalar(fixture, "PRAGMA user_version;"));
        var prior = Values();
        var muted = new UiPreferencesValues(prior.Language, prior.SelectedYear, prior.TermHint,
            prior.DesiredProfile, prior.CloseBehavior, prior.OnboardingVersion, notificationSoundMuted: true);
        var saved = await Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), 0, muted));
        await fixture.ReopenAsync();
        Assert.Equal(saved.CurrentSnapshot, await Preferences(fixture).ReadAsync());
        Assert.True((await Preferences(fixture).ReadAsync()).Values.NotificationSoundMuted);
        using var json = System.Text.Json.JsonDocument.Parse((string)Scalar(fixture, "SELECT valuesJson FROM UiPreferences;")!);
        Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
        Assert.True(json.RootElement.GetProperty("soundMuted").GetBoolean());
    }

    [Fact]
    public async Task SavePersistsSelectedTermAndNamedValuesAcrossReopenWithoutWatchSideEffects()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var command = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, Values());
        var result = await Preferences(fixture).TrySaveAsync(command);
        Assert.Equal(UiPreferencesSaveStatus.Applied, result.Status);
        Assert.Equal(1, result.CurrentSnapshot.Revision);
        Assert.Equal(fixture.Clock.GetUtcNow(), result.CurrentSnapshot.SavedAtUtc);
        await fixture.ReopenAsync();
        var saved = await Preferences(fixture).ReadAsync();
        Assert.Equal(UiLanguage.Korean, saved.Values.Language);
        Assert.Equal(2026, saved.Values.SelectedYear);
        Assert.Equal("U000200002.U000300002", saved.Values.TermHint!.Selector);
        Assert.Equal(WatchMonitorProfile.Faster, saved.Values.DesiredProfile);
        Assert.Equal(UiCloseBehavior.Quit, saved.Values.CloseBehavior);
        Assert.Equal(3, saved.Values.OnboardingVersion);
        Assert.Empty(await fixture.Store.ListSubscriptionsAsync());
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(10));
        Assert.Equal(0L, Scalar(fixture, "SELECT issuedOrdinal+catalogRevision FROM StoreMetadata;"));
        Assert.Equal(0L, Scalar(fixture, "SELECT count(*) FROM SectionState;"));
        Assert.Equal(0L, Scalar(fixture, "SELECT count(*) FROM SourcePolicies;"));
    }

    [Fact]
    public async Task SuccessfulCommandReplayReturnsOriginalReceiptAndCurrentLaterIntentAfterClockRollback()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, Values());
        var saved = await Preferences(fixture).TrySaveAsync(first);
        fixture.Clock.Advance(TimeSpan.FromHours(-1));
        var later = await Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), 1, UiPreferencesValues.Defaults));
        await fixture.ReopenAsync();
        var replay = await Preferences(fixture).TrySaveAsync(first);
        Assert.Equal(UiPreferencesSaveStatus.Replayed, replay.Status);
        Assert.Equal(saved.Receipt!.SavedSnapshot.Revision, replay.Receipt!.SavedSnapshot.Revision);
        Assert.Equal(saved.Receipt.SavedSnapshot.SavedAtUtc, replay.Receipt.SavedSnapshot.SavedAtUtc);
        Assert.Equal(later.CurrentSnapshot.Revision, replay.CurrentSnapshot.Revision);
        Assert.Equal(UiLanguage.English, replay.CurrentSnapshot.Values.Language);
        Assert.Equal(2L, Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    [Fact]
    public async Task SameSuccessfulIdWithChangedFactsThrowsTypedConflictEvenIfExpectedRevisionWouldMatch()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var id = Guid.NewGuid();
        await Preferences(fixture).TrySaveAsync(new(id, 0, Values()));
        await Assert.ThrowsAsync<UiPreferencesReplayConflictException>(() =>
            Preferences(fixture).TrySaveAsync(new(id, 1, UiPreferencesValues.Defaults)));
        Assert.Equal(1, (await Preferences(fixture).ReadAsync()).Revision);
        Assert.Equal(1L, Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    [Fact]
    public async Task TwoStoreInstancesCompareAndSwapOnlyOneIntentAndConflictWritesNoReceipt()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await using var second = await SqliteWatchStore.OpenAsync(new(fixture.DatabasePath), fixture.Clock);
        var results = await Task.WhenAll(Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), 0, Values())),
            ((IUiPreferencesStore)(object)second).TrySaveAsync(new(Guid.NewGuid(), 0, UiPreferencesValues.Defaults)));
        Assert.Single(results, x => x.Status == UiPreferencesSaveStatus.Applied);
        var conflict = Assert.Single(results, x => x.Status == UiPreferencesSaveStatus.VersionConflict);
        Assert.Null(conflict.Receipt);
        Assert.Equal(1, conflict.CurrentSnapshot.Revision);
        Assert.Equal(1L, Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    [Fact]
    public async Task ExplicitSaveOfIdenticalValuesStillAdvancesRevision()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), 0, UiPreferencesValues.Defaults));
        var result = await Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), 1, UiPreferencesValues.Defaults));
        Assert.Equal(UiPreferencesSaveStatus.Applied, result.Status);
        Assert.Equal(2, result.CurrentSnapshot.Revision);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":999}")]
    [InlineData("{\"version\":1,\"language\":\"xx\"}")]
    public async Task CorruptOrFuturePreferencePayloadFailsInsteadOfDefaultingOrOverwriting(string json)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), 0, Values()));
        Write(fixture, "UPDATE UiPreferences SET valuesJson=$json;", ("$json", json));
        await Assert.ThrowsAnyAsync<Exception>(() => Preferences(fixture).ReadAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), 1, Values())));
        Assert.Equal(json, Scalar(fixture, "SELECT valuesJson FROM UiPreferences;"));
        Assert.Equal(1L, Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    [Fact]
    public async Task RevisionOverflowRollsBackWithoutAReceiptOrChangedValues()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), 0, Values()));
        Write(fixture, "UPDATE UiPreferences SET revision=9223372036854775807;");
        await Assert.ThrowsAsync<OverflowException>(() => Preferences(fixture).TrySaveAsync(new(Guid.NewGuid(), long.MaxValue, UiPreferencesValues.Defaults)));
        Assert.Equal(long.MaxValue, (await Preferences(fixture).ReadAsync()).Revision);
        Assert.Equal(UiLanguage.Korean, (await Preferences(fixture).ReadAsync()).Values.Language);
        Assert.Equal(1L, Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    internal static UiPreferencesValues Values() => new(UiLanguage.Korean, 2026,
        new(2026, "U000200002.U000300002"), WatchMonitorProfile.Faster, UiCloseBehavior.Quit, 3);

    internal static object? Scalar(StoreFixture fixture, string sql)
    {
        using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open(SqliteOpenMode.ReadOnly);
        return StoreBatchV2Fixture.Scalar(connection, sql);
    }
    internal static void Write(StoreFixture fixture, string sql, params (string Name, object Value)[] values)
    {
        using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
