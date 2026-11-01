using System.Text.Json.Nodes;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreUiAtomicityTests
{
    [Theory]
    [InlineData(SqliteStoreWriteStage.UiPreferencesValues)]
    [InlineData(SqliteStoreWriteStage.UiPreferencesReceipt)]
    public async Task FaultBetweenPreferenceValuesAndReceiptRollsBackBothAndRetryKeepsOneCommand(SqliteStoreWriteStage target)
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage => { if (armed && stage == target) throw new InjectedFailure(); });
        var preferences = (IUiPreferencesStore)fixture.Store;
        var command = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, StoreUiPreferencesTests.Values());
        armed = true;
        await Assert.ThrowsAsync<InjectedFailure>(() => preferences.TrySaveAsync(command));
        Assert.Equal(0L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM UiPreferences;"));
        Assert.Equal(0L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
        armed = false;
        await fixture.ReopenAsync();
        preferences = (IUiPreferencesStore)fixture.Store;
        Assert.Equal(0, (await preferences.ReadAsync()).Revision);
        Assert.Equal(UiPreferencesSaveStatus.Applied, (await preferences.TrySaveAsync(command)).Status);
        Assert.Equal(UiPreferencesSaveStatus.Replayed, (await preferences.TrySaveAsync(command)).Status);
        Assert.Equal(1L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    [Fact]
    public async Task FaultWhileReplacingSavedPreferencesPreservesPriorIntentAndReceiptAfterReopen()
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        { if (armed && stage == SqliteStoreWriteStage.UiPreferencesReceipt) throw new InjectedFailure(); });
        var original = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, StoreUiPreferencesTests.Values());
        var saved = await ((IUiPreferencesStore)fixture.Store).TrySaveAsync(original);
        armed = true;
        await Assert.ThrowsAsync<InjectedFailure>(() => ((IUiPreferencesStore)fixture.Store).TrySaveAsync(new(Guid.NewGuid(), 1, UiPreferencesValues.Defaults)));
        armed = false;
        await fixture.ReopenAsync();
        var current = await ((IUiPreferencesStore)fixture.Store).ReadAsync();
        Assert.Equal(saved.CurrentSnapshot, current);
        Assert.Equal(UiPreferencesSaveStatus.Replayed, (await ((IUiPreferencesStore)fixture.Store).TrySaveAsync(original)).Status);
        Assert.Equal(1L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    [Fact]
    public async Task FaultAfterTermEvictionRollsBackReplacementEvictionAndGlobalRevisionTogether()
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        { if (armed && stage == SqliteStoreWriteStage.UiTermHints) throw new InjectedFailure(); });
        var cache = (ITermHintCache)fixture.Store;
        for (var index = 0; index < 4; index++)
            await cache.TryReplaceYearAsync(Command(index, 2026 - index, fixture.Clock.GetUtcNow()));
        var original = await cache.ReadAsync();
        armed = true;
        await Assert.ThrowsAsync<InjectedFailure>(() => cache.TryReplaceYearAsync(Command(4, 2022, fixture.Clock.GetUtcNow())));
        armed = false;
        await fixture.ReopenAsync();
        var reopened = await ((ITermHintCache)fixture.Store).ReadAsync();
        Assert.Equal(original.Revision, reopened.Revision);
        Assert.Equal(original.Years.Select(x => x.Year), reopened.Years.Select(x => x.Year));
        Assert.Equal(original.Years.Select(x => x.CacheRevision), reopened.Years.Select(x => x.CacheRevision));
        Assert.DoesNotContain(reopened.Years, x => x.Year == 2022);
    }

    [Fact]
    public async Task CancellationAfterTheDurableCommandStartedRetainsKnownPreferenceReceipt()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        { if (stage == SqliteStoreWriteStage.UiPreferencesReceipt) cancellation.Cancel(); });
        var command = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, StoreUiPreferencesTests.Values());
        var result = await ((IUiPreferencesStore)fixture.Store).TrySaveAsync(command, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(UiPreferencesSaveStatus.Applied, result.Status);
        await fixture.ReopenAsync();
        var replay = await ((IUiPreferencesStore)fixture.Store).TrySaveAsync(command);
        Assert.Equal(UiPreferencesSaveStatus.Replayed, replay.Status);
        Assert.Equal(result.Receipt, replay.Receipt);
    }

    [Theory]
    [InlineData("commandFacts")]
    [InlineData("receipt")]
    public async Task CorruptSuccessfulLedgerIsAStorageFailureRatherThanAnExpectedReplayConflict(string column)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var command = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, StoreUiPreferencesTests.Values());
        await ((IUiPreferencesStore)fixture.Store).TrySaveAsync(command);
        // Column comes only from the fixed test cases; command/payloads remain parameters.
        StoreUiPreferencesTests.Write(fixture, "UPDATE UiPreferenceWrites SET " + column + "=$json;", ("$json", "{\"version\":999}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ((IUiPreferencesStore)fixture.Store).TrySaveAsync(command));
        Assert.Equal(1, (await ((IUiPreferencesStore)fixture.Store).ReadAsync()).Revision);
    }

    [Theory]
    [InlineData("language", "en")]
    [InlineData("profile", "normal")]
    public async Task WellFormedAcceptedFactsContradictingTheirReceiptAreStorageCorruptionBeforeReplayComparison(string field, string replacement)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var preferences = (IUiPreferencesStore)fixture.Store;
        var command = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, StoreUiPreferencesTests.Values());
        var saved = await preferences.TrySaveAsync(command);
        var facts = JsonNode.Parse((string)StoreUiPreferencesTests.Scalar(fixture, "SELECT commandFacts FROM UiPreferenceWrites;")!)!;
        facts["values"]![field] = replacement;
        var altered = facts.ToJsonString();
        StoreUiPreferencesTests.Write(fixture, "UPDATE UiPreferenceWrites SET commandFacts=$facts;", ("$facts", altered));

        await Assert.ThrowsAsync<InvalidDataException>(() => preferences.TrySaveAsync(command));

        Assert.Equal(saved.CurrentSnapshot, await preferences.ReadAsync());
        Assert.Equal(altered, StoreUiPreferencesTests.Scalar(fixture, "SELECT commandFacts FROM UiPreferenceWrites;"));
        Assert.Equal(1L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    private static ReplaceTermHintsCommand Command(long revision, int year, DateTimeOffset at) =>
        new(revision, year, at, [new CatalogTerm(year, "Term.1", "Term", null)]);
    private sealed class InjectedFailure : Exception;
}
