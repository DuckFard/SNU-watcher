using System.Collections.Immutable;
using System.Reflection;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Presentation.Shell;
using SnuWatcher.Windows.Presentation.Watches;
using SnuWatcher.Windows.Tests.Presentation.Common;

namespace SnuWatcher.Windows.Tests.Presentation.Shell;

internal sealed class ShellClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private long timestamp;
    public override DateTimeOffset GetUtcNow() => Now;
    public override long GetTimestamp() => timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    internal void Advance(TimeSpan time) { Now += time; timestamp += time.Ticks; }
}

internal sealed class ShellPorts : IRuntimeCourseQueries, IRuntimeWatchCommands, IWatchDashboardReader,
    IUiPreferencesStore, ITermHintCache, IRuntimeRefreshProfile, ISourceUiControls
{
    internal static SectionKey Key { get; } = new(2026, "2", "1", "F32.102", "012", "000");
    internal UiPreferencesSnapshot Preferences { get; set; } = new(0, UiPreferencesValues.Defaults, null);
    internal SourceStatusSnapshot Source { get; set; } = new(new(0, 0, DateTimeOffset.UtcNow), SourcePolicySnapshot.Initial(SourcePolicyIds.SnuSugang), null);
    internal TermHintCacheSnapshot Terms { get; set; } = new(1, [new CachedTermYear(2026, 1,
        new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), [new("21", "2학기", "Second semester")])]);
    internal Func<CancellationToken, Task<SourceStatusSnapshot>>? SourceHandler { get; set; }
    internal Func<CancellationToken, Task<WatchPageReadResult>>? PageHandler { get; set; }
    internal Func<SaveUiPreferencesCommand, CancellationToken, Task<UiPreferencesSaveResult>>? SaveHandler { get; set; }
    internal List<SaveUiPreferencesCommand> Saves { get; } = [];
    internal List<Guid> DetailsIds { get; } = [];
    internal int HttpCalls { get; private set; }
    internal int PageReads { get; private set; }
    internal int SourceReads { get; private set; }
    internal int Recoveries { get; private set; }
    internal long? ResumedRevision { get; private set; }
    internal SubscriptionSnapshot? Created { get; private set; }
    internal ImmutableArray<CatalogTerm> CatalogTerms { get; set; } = [new(2026, "21", "2학기", null)];
    public WatchMonitorProfileSnapshot AppliedRefreshProfile { get; private set; } = Profile(WatchMonitorProfile.Normal, 0);
    private static WatchMonitorProfileSnapshot Profile(WatchMonitorProfile profile, long revision) => new WatchMonitorLoop(
        DispatchProxy.Create<IWatchStore, UnusedShellPort>(), DispatchProxy.Create<ICourseSource, UnusedShellPort>(),
        DispatchProxy.Create<ISourcePolicyStore, UnusedShellPort>(), TimeProvider.System,
        new(profile, initialPreferencesRevision: revision)).AppliedRefreshProfile;
    internal static WatchMonitorSnapshot InitialRuntime() => new WatchMonitorLoop(
        DispatchProxy.Create<IWatchStore, UnusedShellPort>(), DispatchProxy.Create<ICourseSource, UnusedShellPort>(),
        DispatchProxy.Create<ISourcePolicyStore, UnusedShellPort>(), TimeProvider.System, new()).Snapshot;
    public Task<WatchMonitorProfileSnapshot> SetRefreshProfileAsync(WatchMonitorProfile profile, long preferencesRevision, CancellationToken cancellationToken = default)
    { AppliedRefreshProfile = Profile(profile, preferencesRevision); return Task.FromResult(AppliedRefreshProfile); }
    Task<UiPreferencesSnapshot> IUiPreferencesStore.ReadAsync(CancellationToken token) => Task.FromResult(Preferences);
    public Task<UiPreferencesSaveResult> TrySaveAsync(SaveUiPreferencesCommand command, CancellationToken token = default)
    {
        Saves.Add(command);
        if (SaveHandler is { } handler) return handler(command, token);
        Preferences = new(command.ExpectedRevision + 1, command.Values, DateTimeOffset.UtcNow);
        return Task.FromResult(new UiPreferencesSaveResult(UiPreferencesSaveStatus.Applied,
            new(command.CommandId, command.ExpectedRevision, Preferences), Preferences));
    }
    Task<TermHintCacheSnapshot> ITermHintCache.ReadAsync(CancellationToken token) => Task.FromResult(Terms);
    public Task<TermHintWriteResult> TryReplaceYearAsync(ReplaceTermHintsCommand command, CancellationToken token = default) => throw new NotSupportedException();
    public Task<WatchPageReadResult> ReadWatchPageAsync(WatchPageCursor? after = null, int pageSize = 25, CancellationToken cancellationToken = default)
    { PageReads++; return PageHandler?.Invoke(cancellationToken) ?? Task.FromResult(EmptyPage()); }
    internal static WatchPageReadResult EmptyPage() => new(WatchPageReadDisposition.Page, 0, new(new(0, 0, DateTimeOffset.UtcNow), [], false, null));
    public Task<WatchDetailsSnapshot?> ReadWatchDetailsAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        DetailsIds.Add(subscriptionId);
        return Task.FromResult(Created is { } created && created.Id == subscriptionId
            ? new WatchDetailsSnapshot(new(1, 0, DateTimeOffset.UtcNow), created, [SectionSnapshot.Unchecked(Key)]) : null);
    }
    public Task<SourceStatusSnapshot> ReadSourceStatusAsync(CancellationToken token = default)
    { SourceReads++; return SourceHandler?.Invoke(token) ?? Task.FromResult(Source); }
    public Task<SourceAccessResumeResult> ResumeSourceAccessAsync(long expectedRevision, CancellationToken cancellationToken = default)
    { ResumedRevision = expectedRevision; return Task.FromResult(new SourceAccessResumeResult(true, Source.Policy)); }
    public Task RecoverSourcePolicyAsync(CancellationToken cancellationToken = default) { Recoveries++; return Task.CompletedTask; }
    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, CancellationToken cancellationToken = default)
    { HttpCalls++; return Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete, new(query, CatalogTerms, []), [], DateTimeOffset.UtcNow)); }
    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, CancellationToken cancellationToken = default)
    { HttpCalls++; return Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete, new(query, [new(Key, "통계학", "이권상", null, null, 40, 40, null, null, DateTimeOffset.UtcNow)], 1), [], DateTimeOffset.UtcNow)); }
    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SubscriptionSnapshot> CreateAsync(NewSubscription command, CancellationToken cancellationToken = default)
    {
        Created = new(command.Id, 1, 1, command.Kind, command.Label, WatchStatus.Active, command.InitialNoticeEnabled,
            command.Expiry, command.CreatedAtUtc, command.Sections.Select(key => new MemberSnapshot(key, 0, null)));
        return Task.FromResult(Created);
    }
    public Task<SubscriptionSnapshot> ChangeAsync(SubscriptionChange command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OwnedRefreshResult> RefreshAsync(SectionKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public class UnusedShellPort : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("No actor or source is allowed in this snapshot helper.");
}

internal sealed class ShellFixture : IAsyncDisposable
{
    internal ShellFixture(bool demo = false, bool controls = true)
    {
        Search = new(Ports, Ports, Dispatcher, Clock);
        Watches = new(Ports, Ports, Dispatcher, Clock, () => Ports.AppliedRefreshProfile.TargetInterval);
        Settings = new(Ports, Ports, Dispatcher, Clock);
        Shell = new(Search, Watches, Settings, Ports, Ports, Dispatcher, Clock, demo, controls ? Ports : null);
    }
    internal DedicatedUiDispatcher Dispatcher { get; } = new();
    internal ShellClock Clock { get; } = new();
    internal ShellPorts Ports { get; } = new();
    internal CourseSearchViewModel Search { get; }
    internal WatchListViewModel Watches { get; }
    internal SettingsViewModel Settings { get; }
    internal StudentShellViewModel Shell { get; }
    internal static async Task EventuallyAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(10); }
    }
    public async ValueTask DisposeAsync()
    {
        await Shell.DisposeAsync(); await Search.DisposeAsync(); await Watches.DisposeAsync(); await Settings.DisposeAsync();
        await Dispatcher.DisposeAsync();
    }
}
