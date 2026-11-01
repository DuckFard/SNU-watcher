using System.ComponentModel;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Presentation.Watches;

namespace SnuWatcher.Windows.Presentation.Shell;

/// <summary>Owns shell operations only; the desktop owns and drains injected child controllers and runtime updates.</summary>
public sealed partial class StudentShellViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IWatchDashboardReader dashboard;
    private readonly ITermHintCache termHints;
    private readonly IUiDispatcher dispatcher;
    private readonly TimeProvider clock;
    private readonly ISourceUiControls? controls;
    private readonly Action<string> reportError;
    private readonly UiAsyncCommand[] commands;
    private readonly StudentAlertsPlaceholder alerts = new();
    private SourceStatusSnapshot? storedSource;
    private ShellRuntimeFacts? runtime;
    private RuntimeFailure? failure;
    private UiPreferencesSnapshot? lastPreferences;
    private long runtimeReceivedAt;
    private bool localSavedStateError;
    private volatile bool closed;

    /// <summary>Connects child controllers and typed local ports without performing I/O.</summary>
    /// <param name="search">Desktop-owned search controller.</param>
    /// <param name="watches">Desktop-owned watch controller.</param>
    /// <param name="settings">Desktop-owned settings controller.</param>
    /// <param name="dashboard">Owned runtime dashboard facade.</param>
    /// <param name="termHints">Owned runtime display-hint facade.</param>
    /// <param name="dispatcher">The same serialized UI dispatcher used by all three child controllers.</param>
    /// <param name="clock">Clock used only for local status projection.</param>
    /// <param name="isDemo">Explicit private demonstration label.</param>
    /// <param name="controls">Optional host source-control bridge.</param>
    /// <param name="reportError">Fixed diagnostic observer.</param>
    public StudentShellViewModel(CourseSearchViewModel search, WatchListViewModel watches, SettingsViewModel settings,
        IWatchDashboardReader dashboard, ITermHintCache termHints, IUiDispatcher dispatcher, TimeProvider clock,
        bool isDemo = false, ISourceUiControls? controls = null, Action<string>? reportError = null)
    {
        ArgumentNullException.ThrowIfNull(search); ArgumentNullException.ThrowIfNull(watches); ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dashboard); ArgumentNullException.ThrowIfNull(termHints);
        ArgumentNullException.ThrowIfNull(dispatcher); ArgumentNullException.ThrowIfNull(clock);
        Search = search; Watches = watches; Settings = settings;
        this.dashboard = dashboard; this.termHints = termHints; this.dispatcher = dispatcher; this.clock = clock;
        this.controls = controls; this.reportError = reportError ?? (_ => { });
        Model = new(new(StudentPage.FindCourses, false, isDemo, true, UiLanguage.English, UiCloseBehavior.MinimizeToTray,
            null, ShellSourceProjector.Project(null, null, null, clock.GetUtcNow(), TimeSpan.Zero, false, controls is not null), null), dispatcher, this.reportError);
        LoadCommand = new(LoadCoreAsync, dispatcher, this.reportError);
        RefreshSourceCommand = new(RefreshSourceCoreAsync, dispatcher, this.reportError);
        ResumeAccessCommand = new(ResumeAccessCoreAsync, dispatcher, this.reportError, () => State.Source.CanResumeAccess);
        RecoverPolicyCommand = new(RecoverPolicyCoreAsync, dispatcher, this.reportError, () => State.Source.CanRecoverPolicy);
        CompleteOnboardingCommand = new(CompleteOnboardingCoreAsync, dispatcher, this.reportError, () => State.ShowOnboarding);
        commands = [LoadCommand, RefreshSourceCommand, ResumeAccessCommand, RecoverPolicyCommand, CompleteOnboardingCommand];
        Model.PropertyChanged += ModelChanged;
        Settings.PreferencesChanged += PreferencesAccepted;
        Search.WatchSelection.WatchCreated += WatchCreated;
        Search.Model.PropertyChanged += SearchSelectionChanged;
    }
    /// <summary>Desktop-owned search child.</summary>
    public CourseSearchViewModel Search { get; }
    /// <summary>Desktop-owned watched-section child.</summary>
    public WatchListViewModel Watches { get; }
    /// <summary>Desktop-owned preferences child.</summary>
    public SettingsViewModel Settings { get; }
    /// <summary>Dispatcher-published immutable shell state.</summary>
    public UiSnapshotModel<StudentShellState> Model { get; }
    /// <summary>Latest complete shell rendering snapshot.</summary>
    public StudentShellState State => Model.State;
    /// <summary>Navigation binding; setters require the UI dispatcher.</summary>
    public StudentPage SelectedPage { get => State.SelectedPage; set => Navigate(value); }
    /// <summary>Typed child controller or honest inactive-alert placeholder for a desktop data template.</summary>
    public object PageContent => SelectedPage switch
    { StudentPage.FindCourses => Search, StudentPage.MyWatches => Watches, StudentPage.Settings => Settings, _ => alerts };
    /// <summary>Reads local preferences, hints and source status only.</summary>
    public UiAsyncCommand LoadCommand { get; }
    /// <summary>Rereads durable source status without HTTP.</summary>
    public UiAsyncCommand RefreshSourceCommand { get; }
    /// <summary>Explicit revision-checked access resume through the owning host.</summary>
    public UiAsyncCommand ResumeAccessCommand { get; }
    /// <summary>Explicit local source-policy finalization retry through the owning host.</summary>
    public UiAsyncCommand RecoverPolicyCommand { get; }
    /// <summary>Saves known editor changes before recording onboarding completion.</summary>
    public UiAsyncCommand CompleteOnboardingCommand { get; }
    /// <summary>Binding notifications raised only on the UI dispatcher.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Known saved preferences for desktop language/tray composition; observer faults cannot undo them.</summary>
    public event Action<UiPreferencesSnapshot>? PreferencesChanged;
    /// <summary>Runs the owned local startup command and returns its actual command outcome.</summary>
    public Task<UiCommandOutcome> LoadInitialAsync(CancellationToken cancellationToken = default) => LoadCommand.ExecuteAsync(cancellationToken);
    /// <summary>Navigates on the dispatcher; child reloads retain their own operation ownership.</summary>
    public void Navigate(StudentPage page)
    {
        RequireUi(); ObjectDisposedException.ThrowIf(closed, this);
        if (!Enum.IsDefined(page)) throw new ArgumentOutOfRangeException(nameof(page));
        Model.Replace(State with { SelectedPage = page });
        if (page == StudentPage.MyWatches) RequestWatchReload(force: true);
        if (page == StudentPage.Settings) Settings.ReloadCommand.Execute(null);
    }

    /// <summary>The desktop is the sole runtime-channel consumer and calls this on its dispatcher.</summary>
    public void UpdateRuntime(WatchMonitorSnapshot? snapshot, RuntimeFailure? failure, long? committedGeneration = null)
    {
        RequireUi();
        if (closed) return;
        this.failure ??= failure;
        if (snapshot is not null)
        {
            var next = snapshot.Queries.Select(query => query.NextCheckIn).Where(value => value is not null).Min();
            runtime = new(snapshot.IsOnline, snapshot.IsSuspended, snapshot.ReasonCode, next);
            runtimeReceivedAt = clock.GetTimestamp();
        }
        PublishSource();
        if (committedGeneration is { } generation)
        {
            if (generation > dirtyGeneration) dirtyGeneration = generation;
            RequestWatchReload(force: false);
        }
    }

    /// <summary>Reprojects retained facts with local elapsed time; no source or persistence call occurs.</summary>
    public void RefreshAges()
    {
        RequireUi();
        if (closed) return;
        Watches.RefreshAges(); PublishSource();
    }

    private void PublishSource()
    {
        var elapsed = runtime is null ? TimeSpan.Zero : clock.GetElapsedTime(runtimeReceivedAt, clock.GetTimestamp());
        Model.Replace(State with { Source = ShellSourceProjector.Project(storedSource, runtime, failure,
            clock.GetUtcNow(), elapsed, localSavedStateError, controls is not null) });
        ResumeAccessCommand.NotifyCanExecuteChanged(); RecoverPolicyCommand.NotifyCanExecuteChanged();
    }

    private void PreferencesAccepted(UiPreferencesSnapshot snapshot)
    {
        RequireUi();
        if (closed || snapshot == lastPreferences || lastPreferences is { } previous && snapshot.Revision < previous.Revision) return;
        lastPreferences = snapshot;
        Model.Replace(State with { Language = snapshot.Values.Language, CloseBehavior = snapshot.Values.CloseBehavior,
            ShowOnboarding = snapshot.Values.OnboardingVersion < 1 });
        CompleteOnboardingCommand.NotifyCanExecuteChanged();
        foreach (var observer in PreferencesChanged?.GetInvocationList() ?? [])
            try { ((Action<UiPreferencesSnapshot>)observer)(snapshot); }
            catch (Exception) { Report("ui_shell_observer_failed"); }
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var property in new[] { nameof(State), nameof(SelectedPage), nameof(PageContent) })
            foreach (var observer in PropertyChanged?.GetInvocationList() ?? [])
                try { ((PropertyChangedEventHandler)observer)(this, new(property)); }
                catch (Exception) { Report("ui_shell_observer_failed"); }
    }

    private void RequireUi() { if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Shell changes require the UI dispatcher."); }
    private void Report(string code) { try { reportError(code); } catch (Exception) { } }
}
