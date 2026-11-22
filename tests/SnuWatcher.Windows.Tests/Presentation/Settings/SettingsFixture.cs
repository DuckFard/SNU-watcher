using System.Collections.Immutable;
using System.Reflection;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Sources;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Tests.Presentation.Common;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

internal sealed class SettingsClock : TimeProvider
{
    internal static DateTimeOffset Now { get; } = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class SettingsStore : IUiPreferencesStore
{
    private readonly object sync = new();
    private ImmutableArray<SaveUiPreferencesCommand> commands = [];
    private ImmutableDictionary<Guid, UiPreferencesSaveReceipt> receipts = ImmutableDictionary<Guid, UiPreferencesSaveReceipt>.Empty;
    private UiPreferencesSnapshot current = new(0, UiPreferencesValues.Defaults, null);
    internal Func<CancellationToken, Task<UiPreferencesSnapshot>>? ReadHandler { get; set; }
    internal Func<SaveUiPreferencesCommand, CancellationToken, Task<UiPreferencesSaveResult>>? SaveHandler { get; set; }
    internal Action<SaveUiPreferencesCommand>? Saved { get; set; }
    internal int Reads { get; private set; }
    internal UiPreferencesSnapshot Current { get { lock (sync) return current; } }
    internal ImmutableArray<SaveUiPreferencesCommand> Commands { get { lock (sync) return commands; } }
    public Task<UiPreferencesSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) Reads++;
        return ReadHandler?.Invoke(cancellationToken) ?? Task.FromResult(Current);
    }
    public Task<UiPreferencesSaveResult> TrySaveAsync(SaveUiPreferencesCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) commands = commands.Add(command);
        Saved?.Invoke(command);
        return SaveHandler?.Invoke(command, cancellationToken) ?? Task.FromResult(Commit(command));
    }
    internal UiPreferencesSaveResult Commit(SaveUiPreferencesCommand command)
    {
        lock (sync)
        {
            if (receipts.TryGetValue(command.CommandId, out var receipt))
                return new(UiPreferencesSaveStatus.Replayed, receipt, current);
            if (command.ExpectedRevision != current.Revision)
                return new(UiPreferencesSaveStatus.VersionConflict, null, current);
            var next = new UiPreferencesSnapshot(current.Revision + 1, command.Values, SettingsClock.Now);
            receipt = new(command.CommandId, current.Revision, next);
            current = next;
            receipts = receipts.Add(command.CommandId, receipt);
            return new(UiPreferencesSaveStatus.Applied, receipt, current);
        }
    }
    internal void ExternalSave(UiPreferencesValues values) => Commit(new(Guid.NewGuid(), Current.Revision, values));
}

internal sealed class SettingsProfile : IRuntimeRefreshProfile
{
    private readonly object sync = new();
    private WatchMonitorProfileSnapshot applied = Snapshot(WatchMonitorProfile.Normal, 0);
    private ImmutableArray<(WatchMonitorProfile Profile, long Revision)> calls = [];
    internal Func<WatchMonitorProfile, long, CancellationToken, Task<WatchMonitorProfileSnapshot>>? Handler { get; set; }
    internal Action? Applying { get; set; }
    internal ImmutableArray<(WatchMonitorProfile Profile, long Revision)> Calls { get { lock (sync) return calls; } }
    public WatchMonitorProfileSnapshot AppliedRefreshProfile { get { lock (sync) return applied; } }
    public async Task<WatchMonitorProfileSnapshot> SetRefreshProfileAsync(WatchMonitorProfile profile, long preferencesRevision,
        CancellationToken cancellationToken = default)
    {
        lock (sync) calls = calls.Add((profile, preferencesRevision));
        Applying?.Invoke();
        var value = Handler is { } handler ? await handler(profile, preferencesRevision, cancellationToken)
            : Snapshot(profile, preferencesRevision);
        lock (sync) applied = value;
        return value;
    }
    internal static WatchMonitorProfileSnapshot Snapshot(WatchMonitorProfile profile, long revision)
    {
        // Obtain an actual validated snapshot without starting an actor; any dependency call fails.
        var loop = new WatchMonitorLoop(DispatchProxy.Create<IWatchStore, UnusedSettingsPort>(),
            DispatchProxy.Create<ICourseSource, UnusedSettingsPort>(), DispatchProxy.Create<ISourcePolicyStore, UnusedSettingsPort>(),
            new SettingsClock(), new(profile, initialPreferencesRevision: revision));
        return loop.AppliedRefreshProfile;
    }
}

public class UnusedSettingsPort : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new InvalidOperationException("A snapshot-only test must not start an engine or source.");
}

internal sealed class SettingsFixture : IAsyncDisposable
{
    internal SettingsFixture(Action? nativeTest = null)
    {
        ViewModel = new(Store, Profile, Dispatcher, new SettingsClock(), nativeTest, Errors.Add);
    }
    internal DedicatedUiDispatcher Dispatcher { get; } = new();
    internal SettingsStore Store { get; } = new();
    internal SettingsProfile Profile { get; } = new();
    internal List<string> Errors { get; } = [];
    internal SettingsViewModel ViewModel { get; }
    internal Task EditAsync(UiLanguage language = UiLanguage.Korean, WatchMonitorProfile profile = WatchMonitorProfile.Faster,
        UiCloseBehavior close = UiCloseBehavior.Quit) => Dispatcher.InvokeAsync(() =>
    { ViewModel.Language = language; ViewModel.DesiredProfile = profile; ViewModel.CloseBehavior = close; });
    public async ValueTask DisposeAsync()
    {
        await ViewModel.DisposeAsync();
        await Dispatcher.DisposeAsync();
    }
}
