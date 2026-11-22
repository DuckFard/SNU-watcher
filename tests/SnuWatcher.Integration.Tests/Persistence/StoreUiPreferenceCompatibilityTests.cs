using Microsoft.Data.Sqlite;
using System.Text.Json;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreUiPreferenceCompatibilityTests
{
    [Fact]
    public async Task VersionFourMustBeBackedUpAndUpgradedBeforeAnySoundAwareWrite()
    {
        using var original = new StoreHistoryV4Fixture();
        var before = original.LogicalSnapshot();
        await using var store = await SqliteWatchStore.OpenAsync(original.Factory, new StoreClock());
        Assert.Equal(5, original.Version);
        Assert.Equal(UiRows(before), UiRows(original.LogicalSnapshot()));
        var backup = Assert.Single(original.Backups());
        using var source = new SqliteConnectionFactory(backup).Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(4L, StoreBatchV2Fixture.Scalar(source, "PRAGMA user_version;"));
        Assert.Equal(before, StoreHistoryV4Fixture.LogicalSnapshot(source));
        Assert.False((await ((IUiPreferencesStore)store).ReadAsync()).Values.NotificationSoundMuted);
        AssertLegacyLedger(original);
        var saved = await store.TrySaveAsync(new(Guid.NewGuid(), 1, Muted()));
        Assert.True(saved.CurrentSnapshot.Values.NotificationSoundMuted);
        AssertLegacyLedger(original);
        Assert.Equal(before, StoreHistoryV4Fixture.LogicalSnapshot(source));
    }

    [Fact]
    public async Task LegacyReplayKeepsOriginalReceiptBytesAndReturnsNewerMutedCurrentIntent()
    {
        using var original = new StoreHistoryV4Fixture();
        var clock = new StoreClock();
        await using var store = await SqliteWatchStore.OpenAsync(original.Factory, clock);
        Assert.Equal(5, original.Version); Assert.Single(original.Backups());
        var legacy = new SaveUiPreferencesCommand(Guid.ParseExact(StoreHistoryV4Fixture.PreferencesCommandId, "N"), 0,
            new(UiLanguage.Korean, 2026, new(2026, "U000200002.U000300002"), WatchMonitorProfile.Faster, UiCloseBehavior.Quit, 1));
        var later = await store.TrySaveAsync(new(Guid.NewGuid(), 1, Muted()));
        clock.Advance(TimeSpan.FromHours(-1));
        var replayed = await store.TrySaveAsync(legacy);
        Assert.Equal(UiPreferencesSaveStatus.Replayed, replayed.Status);
        Assert.Equal(1, replayed.Receipt!.SavedSnapshot.Revision);
        Assert.False(replayed.Receipt.SavedSnapshot.Values.NotificationSoundMuted);
        Assert.Equal(DateTimeOffset.Parse(StoreUiV3Fixture.Time), replayed.Receipt.SavedSnapshot.SavedAtUtc);
        Assert.Equal(later.CurrentSnapshot, replayed.CurrentSnapshot);
        Assert.True(replayed.CurrentSnapshot.Values.NotificationSoundMuted);
        AssertLegacyLedger(original);
        var changed = legacy.Values;
        await Assert.ThrowsAsync<UiPreferencesReplayConflictException>(() => store.TrySaveAsync(new(legacy.CommandId, 0,
            new(changed.Language, changed.SelectedYear, changed.TermHint, changed.DesiredProfile, changed.CloseBehavior,
                changed.OnboardingVersion, notificationSoundMuted: true))));
        Assert.Equal(later.CurrentSnapshot, await ((IUiPreferencesStore)store).ReadAsync());
        using var connection = original.Factory.Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(2L, StoreBatchV2Fixture.Scalar(connection, "SELECT count(*) FROM UiPreferenceWrites;"));
        AssertLegacyLedger(original);
    }

    private static UiPreferencesValues Muted() => new(UiLanguage.English, null, null,
        WatchMonitorProfile.LowActivity, UiCloseBehavior.MinimizeToTray, 1, notificationSoundMuted: true);

    private static string UiRows(string logicalSnapshot)
    {
        using var document = JsonDocument.Parse(logicalSnapshot);
        return document.RootElement.GetProperty("tables").GetRawText();
    }

    private static void AssertLegacyLedger(StoreHistoryV4Fixture original)
    {
        using var connection = original.Factory.Open(SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT commandFacts,receipt FROM UiPreferenceWrites WHERE commandId=$id;";
        command.Parameters.AddWithValue("$id", StoreHistoryV4Fixture.PreferencesCommandId);
        using var reader = command.ExecuteReader(); Assert.True(reader.Read());
        Assert.Equal(StoreHistoryV4Fixture.CommandJson, reader.GetString(0));
        Assert.Equal(StoreHistoryV4Fixture.ReceiptJson, reader.GetString(1));
    }
}
