using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Notifications;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Navigation;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Presentation.Shell;
using SnuWatcher.Windows.Presentation.Watches;
using SnuWatcher.Windows.Diagnostics;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>
/// Owns presentation controllers and their update lifetimes. The application separately owns the
/// Windows host and must keep the dispatcher alive until this session's actual disposal completes.
/// </summary>
internal sealed class StudentDesktopSession : IAsyncDisposable
{
    private readonly object _lifetime = new();
    private readonly WindowsMonitoringHost _host;
    private readonly IUiDispatcher _dispatcher;
    private readonly DispatcherTimer _ageTimer;
    private readonly SystemStateBridge? _systemState;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<UiCommandOutcome>? _initialization;
    private Task _updates = Task.CompletedTask;
    private Task? _disposal;
    private volatile bool _closing;
    private string? _lastErrorCode;

    public StudentDesktopSession(WindowsMonitoringHost host, IUiDispatcher dispatcher, TimeProvider clock,
        IStudentActions actions, Action nativeTest, bool isDemo = false, bool observeSystemState = true,
        INativeNotificationControls? nativeNotifications = null)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(nativeTest);
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Create the student interface on its UI dispatcher.");
        _host = host; _dispatcher = dispatcher;
        Search = new(host.Courses, host.WatchCommands, dispatcher, clock, Report, actions);
        Watches = new(host.Dashboard, host.WatchCommands, dispatcher, clock,
            () => host.RefreshProfile.AppliedRefreshProfile.TargetInterval);
        SettingsViewModel? settings = null;
        var diagnostics = new WindowsDiagnosticsReader(host.StorageDiagnostics, () =>
        {
            var snapshot = host.Session.LatestSnapshot;
            var state = settings?.Model.State;
            return DiagnosticsStateProjector.Project(host.Session.State, snapshot?.ReasonCode, snapshot?.IsOnline,
                snapshot?.IsSuspended, host.Session.Failure?.Kind, snapshot?.AppliedRefreshProfile.Profile,
                state?.Stored?.Values, state?.NativeStatus?.Readiness);
        }, typeof(StudentDesktopSession).Assembly.GetName().Version ?? new Version(0, 0), clock);
        Settings = settings = new(host.Preferences, host.RefreshProfile, dispatcher, clock, nativeTest, Report, nativeNotifications,
            diagnostics, new DiagnosticsExportAction(dispatcher));
        Alerts = new(host.Alerts, dispatcher, clock, Report, actions);
        Shell = new(Search, Watches, Settings, host.Dashboard, host.TermHints, dispatcher, clock,
            isDemo, new DesktopSourceControls(host), Report, Alerts);
        _ageTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
            { Interval = TimeSpan.FromSeconds(2) };
        _ageTimer.Tick += RefreshAges;
        if (observeSystemState && !isDemo) _systemState = new(host, Report);
    }

    public StudentShellViewModel Shell { get; }
    public CourseSearchViewModel Search { get; }
    public WatchListViewModel Watches { get; }
    public SettingsViewModel Settings { get; }
    public AlertHistoryViewModel Alerts { get; }
    public string? LastErrorCode => Volatile.Read(ref _lastErrorCode);
    /// <summary>First fixed presentation failure, independent of any lossy visual status update.</summary>
    public Task<string> FailureTask => _failure.Task;

    /// <summary>One local startup. Caller cancellation detaches its waiter; disposal owns cancellation and actual drain.</summary>
    public Task<UiCommandOutcome> InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<UiCommandOutcome> initialization;
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_initialization is null)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _initialization = InitializeCoreAsync(ready.Task);
                ready.SetResult();
            }
            initialization = _initialization;
        }
        return initialization.WaitAsync(cancellationToken);
    }

    private async Task<UiCommandOutcome> InitializeCoreAsync(Task ready)
    {
        await ready.ConfigureAwait(false);
        try
        {
            Task<UiCommandOutcome>? loading = null;
            await _dispatcher.InvokeAsync(() => loading = Shell.LoadInitialAsync(_stop.Token), _stop.Token).ConfigureAwait(false);
            var outcome = await loading!.ConfigureAwait(false);
            if (outcome != UiCommandOutcome.Completed) return outcome;
            if (!Shell.State.IsLoaded) { Report("desktop_initialization_failed"); return UiCommandOutcome.Failed; }
            await _dispatcher.InvokeAsync(() =>
            {
                lock (_lifetime)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    if (_closing) throw new OperationCanceledException(_stop.Token);
                    Shell.UpdateRuntime(_host.Session.LatestSnapshot, _host.Session.Failure);
                    var updatesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _updates = ReadUpdatesAsync(updatesReady.Task);
                    try { _ageTimer.Start(); _systemState?.Start(); }
                    finally { updatesReady.SetResult(); } // Partial OS startup must not strand the retained reader.
                }
            }, _stop.Token).ConfigureAwait(false);
            return UiCommandOutcome.Completed;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return UiCommandOutcome.Canceled; }
        catch (Exception) { Report("desktop_initialization_failed"); return UiCommandOutcome.Failed; }
    }

    private async Task ReadUpdatesAsync(Task ready)
    {
        await ready.ConfigureAwait(false);
        try
        {
            await foreach (var update in _host.Session.Updates.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                // Await each publication: at most one UI operation and the backend's bounded latest hint are retained.
                await _dispatcher.InvokeAsync(() =>
                {
                    if (!_closing) Shell.UpdateRuntime(update.Snapshot, update.Failure, update.CommittedGeneration);
                }, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { Report("desktop_runtime_updates_failed"); }
    }

    private void RefreshAges(object? sender, EventArgs args)
    {
        if (_closing) return;
        try { Shell.RefreshAges(); }
        catch (Exception) { Report("desktop_age_update_failed"); }
    }

    private void Report(string code)
    {
        var bounded = code is { Length: > 0 and <= 64 }
            && code.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-')
                ? code : "desktop_presentation_failed";
        Volatile.Write(ref _lastErrorCode, bounded);
        _failure.TrySetResult(bounded);
    }

    /// <summary>Closes only this UI graph; the caller owns and drains the host independently.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifetime)
        {
            if (_disposal is null)
            {
                _closing = true;
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = DisposeCoreAsync(ready.Task, _initialization ?? Task.FromResult(UiCommandOutcome.Unavailable), _updates);
                ready.SetResult();
            }
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Task ready, Task initialization, Task updates)
    {
        await ready.ConfigureAwait(false);
        var drains = new List<Task>
        {
            BeginDrain(() => _stop.CancelAsync()),
            BeginDrain(() => _dispatcher.InvokeAsync(() => { _ageTimer.Stop(); _ageTimer.Tick -= RefreshAges; })),
        };
        // Shell routes can await child commands. Start EVERY disposal before awaiting any of them.
        drains.Add(BeginDrain(() => Shell.DisposeAsync().AsTask()));
        drains.Add(BeginDrain(() => Search.DisposeAsync().AsTask()));
        drains.Add(BeginDrain(() => Watches.DisposeAsync().AsTask()));
        drains.Add(BeginDrain(() => Settings.DisposeAsync().AsTask()));
        drains.Add(BeginDrain(() => Alerts.DisposeAsync().AsTask()));
        if (_systemState is not null) drains.Add(BeginDrain(() => _systemState.DisposeAsync().AsTask()));
        drains.Add(initialization); drains.Add(updates);
        try { await Task.WhenAll(drains).ConfigureAwait(false); }
        catch (Exception)
        {
            Report("desktop_dispose_failed");
            throw new InvalidOperationException("The student interface could not finish stopping.");
        }
        finally { _stop.Dispose(); }
    }

    private static Task BeginDrain(Func<Task> begin)
    {
        try { return begin(); }
        catch (Exception error) { return Task.FromException(error); }
    }
}
