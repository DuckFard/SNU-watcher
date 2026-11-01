using System.ComponentModel;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Settings;

/// <summary>Local preference intent and actor application share one bounded owned UI workflow.</summary>
public sealed partial class SettingsViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IUiPreferencesStore preferences;
    private readonly IRuntimeRefreshProfile refreshProfile;
    private readonly IUiDispatcher dispatcher;
    private readonly TimeProvider clock;
    private readonly Action<string> reportError;
    private readonly SemaphoreSlim operationLane = new(1, 1);
    private readonly object lifetimeSync = new();
    private readonly CancellationTokenSource hintStop = new();
    private readonly UiAsyncCommand[] commands;
    private volatile bool closed;
    private long editVersion;
    private Task? disposal;

    /// <summary>Uses owned session views and a pure root-supplied notification-test callback.</summary>
    public SettingsViewModel(IUiPreferencesStore preferences, IRuntimeRefreshProfile refreshProfile,
        IUiDispatcher dispatcher, TimeProvider clock, Action? requestNativeTest = null, Action<string>? reportError = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(refreshProfile);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        this.preferences = preferences; this.refreshProfile = refreshProfile; this.dispatcher = dispatcher;
        this.clock = clock; this.reportError = reportError ?? (_ => { });
        Model = new(SettingsSnapshot.Initial, dispatcher, reportError);
        ReloadCommand = Command(ReloadCoreAsync, () => !Model.State.IsBusy);
        SaveCommand = Command(SaveCoreAsync, () => Model.State.IsLoaded && !Model.State.IsBusy);
        RetryApplyCommand = Command(RetryApplyCoreAsync,
            () => Model.State.IsApplyPending && !Model.State.SaveUncertain && !Model.State.IsBusy);
        DiscardChangesCommand = Command(DiscardCoreAsync,
            () => Model.State.IsLoaded && Model.State.IsDirty && !Model.State.SaveUncertain && !Model.State.IsBusy);
        NativeTestCommand = new(token => dispatcher.InvokeAsync(() => requestNativeTest!(), token), dispatcher,
            this.reportError, () => requestNativeTest is not null);
        commands = [ReloadCommand, SaveCommand, RetryApplyCommand, DiscardChangesCommand, NativeTestCommand];
        Model.PropertyChanged += ModelChanged;
    }
    /// <summary>Dispatcher-published immutable settings state.</summary>
    public UiSnapshotModel<SettingsSnapshot> Model { get; }
    /// <summary>Reloads saved values while preserving unsaved edits and uncertain command identity.</summary>
    public UiAsyncCommand ReloadCommand { get; }
    /// <summary>Saves editable intent, or retries the identical unresolved write, before applying the saved profile.</summary>
    public UiAsyncCommand SaveCommand { get; }
    /// <summary>Retries actor application without another preference save.</summary>
    public UiAsyncCommand RetryApplyCommand { get; }
    /// <summary>Explicitly replaces unsaved edits with the latest known saved choices.</summary>
    public UiAsyncCommand DiscardChangesCommand { get; }
    /// <summary>Invokes only the optional root-provided callback on the dispatcher.</summary>
    public UiAsyncCommand NativeTestCommand { get; }
    /// <summary>The editable language; setters require this model's dispatcher.</summary>
    public UiLanguage Language
    { get => Model.State.Editor.Language; set { ValidateEnum(value); Edit(Model.State.Editor with { Language = value }); } }
    /// <summary>The editable target profile, distinct from the actor's actual applied profile.</summary>
    public WatchMonitorProfile DesiredProfile
    { get => Model.State.Editor.DesiredProfile; set { ValidateEnum(value); Edit(Model.State.Editor with { DesiredProfile = value }); } }
    /// <summary>The editable close behavior; it takes effect after a known save.</summary>
    public UiCloseBehavior CloseBehavior
    { get => Model.State.Editor.CloseBehavior; set { ValidateEnum(value); Edit(Model.State.Editor with { CloseBehavior = value }); } }
    /// <summary>Binding notifications run on the dispatcher and isolate observer exceptions.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Known saved/read preferences, published before actor application for shell language/tray updates.</summary>
    public event Action<UiPreferencesSnapshot>? PreferencesChanged;

    /// <summary>Reads local preferences and the actor's actual snapshot; this never issues a source request.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = await ReloadCommand.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (outcome == UiCommandOutcome.Failed) throw new InvalidOperationException("settings_load_failed");
        if (outcome == UiCommandOutcome.Canceled) cancellationToken.ThrowIfCancellationRequested();
    }

    private UiAsyncCommand Command(Func<CancellationToken, Task> operation, Func<bool> available) =>
        new(token => RunSerializedAsync(operation, token), dispatcher, reportError, available);

    private static void ValidateEnum<T>(T value) where T : struct, Enum
    { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); }

    private void Edit(SettingsEditor editor)
    {
        ThrowIfClosed();
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Settings edits require the UI dispatcher.");
        if (Model.State.SaveUncertain) throw new InvalidOperationException("Resolve the uncertain save before editing settings.");
        if (editor == Model.State.Editor) return;
        editVersion = checked(editVersion + 1);
        Model.Replace(Model.State with { Editor = editor });
    }

    private void ThrowIfClosed()
    { if (closed) throw new ObjectDisposedException(nameof(SettingsViewModel)); }

    private Task UpdateAsync(Func<SettingsSnapshot, SettingsSnapshot> update, Action? after = null) =>
        dispatcher.InvokeAsync(() => { Model.Replace(update(Model.State)); after?.Invoke(); });

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var property in new[] { nameof(Language), nameof(DesiredProfile), nameof(CloseBehavior) })
            foreach (var observer in PropertyChanged?.GetInvocationList() ?? [])
                try { ((PropertyChangedEventHandler)observer)(this, new(property)); }
                catch (Exception) { ReportObserverFailure(); }
        foreach (var command in commands) command.NotifyCanExecuteChanged();
    }

    private void PublishPreferences(UiPreferencesSnapshot snapshot)
    {
        foreach (var observer in PreferencesChanged?.GetInvocationList() ?? [])
            try { ((Action<UiPreferencesSnapshot>)observer)(snapshot); }
            catch (Exception) { ReportObserverFailure(); }
    }

    private void ReportObserverFailure()
    {
        try { reportError("ui_settings_observer_failed"); }
        catch (Exception) { System.Diagnostics.Trace.WriteLine("ui_settings_observer_failed"); }
    }

    private async Task RunSerializedAsync(Func<CancellationToken, Task> operation, CancellationToken token) =>
        await RunSerializedAsync(async cancellation => { await operation(cancellation).ConfigureAwait(false); return true; }, token).ConfigureAwait(false);

    private async Task<T> RunSerializedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        await operationLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            await UpdateAsync(state => state with { IsBusy = true }).ConfigureAwait(false);
            return await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await UpdateAsync(state => state with { IssueKey = state.SaveUncertain ? "Ui.SettingsSaveUncertain"
                : state.HasConflict ? "Ui.SettingsConflict" : "Ui.CommandFailed" }).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try { await UpdateAsync(state => state with { IsBusy = false, LastOperationAtUtc = clock.GetUtcNow() }).ConfigureAwait(false); }
            finally { operationLane.Release(); }
        }
    }
}
