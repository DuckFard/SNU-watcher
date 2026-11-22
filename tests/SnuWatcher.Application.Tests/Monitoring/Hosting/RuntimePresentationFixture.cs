using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

/// <summary>Scripted typed port responses; this is not an in-memory store or preference implementation.</summary>
internal sealed class RuntimePresentationPorts : IWatchDashboardReader, IUiPreferencesStore, ITermHintCache
{
    internal RuntimePresentationServices Services => new(this, this, this);
    internal List<object> Calls { get; } = [];
    internal Func<CancellationToken, Task<UiPreferencesSnapshot>> ReadPreferences { get; set; } = _ =>
        Task.FromResult(new UiPreferencesSnapshot(0, UiPreferencesValues.Defaults, null));
    internal Func<CancellationToken, Task<TermHintCacheSnapshot>> ReadTerms { get; set; } = _ =>
        Task.FromResult(new TermHintCacheSnapshot(0, []));
    internal Func<SaveUiPreferencesCommand, CancellationToken, Task<UiPreferencesSaveResult>> Save { get; set; } = (command, _) =>
    {
        var saved = new UiPreferencesSnapshot(command.ExpectedRevision + 1, command.Values, MonitorFixture.Now);
        return Task.FromResult(new UiPreferencesSaveResult(UiPreferencesSaveStatus.Applied,
            new(command.CommandId, command.ExpectedRevision, saved), saved));
    };
    internal Func<ReplaceTermHintsCommand, CancellationToken, Task<TermHintWriteResult>> Replace { get; set; } = (_, _) =>
        Task.FromResult(new TermHintWriteResult(TermHintWriteStatus.VersionConflict, Terms(8)));

    internal static TermHintCacheSnapshot Terms(long revision) => new(revision,
        [new CachedTermYear(2026, revision, MonitorFixture.Now.AddDays(-2), [new("21", "2학기", "Second semester")])]);

    public Task<WatchPageReadResult> ReadWatchPageAsync(WatchPageCursor? after = null, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        Calls.Add((after, pageSize));
        return Task.FromResult(new WatchPageReadResult(WatchPageReadDisposition.RestartRequired, 8, null));
    }
    public Task<WatchDetailsSnapshot?> ReadWatchDetailsAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    { Calls.Add(subscriptionId); return Task.FromResult<WatchDetailsSnapshot?>(null); }
    public Task<SourceStatusSnapshot> ReadSourceStatusAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("source_status");
        return Task.FromResult(new SourceStatusSnapshot(new(0, 0, MonitorFixture.Now),
            SourcePolicySnapshot.Initial(SourcePolicyIds.SnuSugang), null));
    }
    Task<UiPreferencesSnapshot> IUiPreferencesStore.ReadAsync(CancellationToken token)
    { Calls.Add("preferences_read"); return ReadPreferences(token); }
    public Task<UiPreferencesSaveResult> TrySaveAsync(SaveUiPreferencesCommand command, CancellationToken token)
    { Calls.Add(command); return Save(command, token); }
    Task<TermHintCacheSnapshot> ITermHintCache.ReadAsync(CancellationToken token)
    { Calls.Add("terms_read"); return ReadTerms(token); }
    public Task<TermHintWriteResult> TryReplaceYearAsync(ReplaceTermHintsCommand command, CancellationToken token)
    { Calls.Add(command); return Replace(command, token); }
}
