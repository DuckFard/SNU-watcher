using System.ComponentModel;
using System.Collections.Immutable;
using System.Threading.Channels;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Navigation;

namespace SnuWatcher.Windows.Presentation.Alerts;

/// <summary>Owns bounded history reads and UI actions, independently of monitoring or native delivery.</summary>
public sealed partial class AlertHistoryViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IAlertHistoryReader reader;
    private readonly IUiDispatcher dispatcher;
    private readonly TimeProvider clock;
    private readonly Action<string>? reportError;
    private readonly IStudentActions? actions;
    private readonly UiAsyncCommand selectionCommand;
    private readonly ImmutableArray<UiAsyncCommand> commands;
    private readonly ImmutableArray<UiAsyncCommand> pageCommands;
    private readonly SemaphoreSlim pageGate = new(1, 1);
    private readonly SemaphoreSlim recipientGate = new(1, 1);
    private readonly SemaphoreSlim attemptGate = new(1, 1);
    private readonly object disposalSync = new();
    private readonly Channel<long> selectionRequests = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task selectionWorker;
    private AlertNavigation<AlertHistoryCursor> navigation = AlertNavigation<AlertHistoryCursor>.Empty;
    private AlertNavigation<AlertRecipientCursor> recipientNavigation = AlertNavigation<AlertRecipientCursor>.Empty;
    private AlertNavigation<AlertAttemptCursor> attemptNavigation = AlertNavigation<AlertAttemptCursor>.Empty;
    private long selectionGeneration;
    private long settledSelectionGeneration;
    private TaskCompletionSource<bool>? selectionReady;
    private Task? disposal;
    private volatile bool stopping;

    /// <summary>Creates a presentation over the owned history facade and optional fixed desktop actions.</summary>
    public AlertHistoryViewModel(IAlertHistoryReader reader, IUiDispatcher dispatcher, TimeProvider clock,
        Action<string>? reportError = null, IStudentActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(dispatcher); ArgumentNullException.ThrowIfNull(clock);
        this.reader = reader; this.dispatcher = dispatcher; this.clock = clock; this.reportError = reportError; this.actions = actions;
        Model = new(new(), dispatcher, reportError);
        LoadCommand = PageCommand(AlertPageMove.Load);
        ReloadCommand = PageCommand(AlertPageMove.Reload);
        LatestCommand = PageCommand(AlertPageMove.Latest);
        NewerCommand = PageCommand(AlertPageMove.Newer);
        OlderCommand = PageCommand(AlertPageMove.Older);
        pageCommands = [LoadCommand, ReloadCommand, LatestCommand, NewerCommand, OlderCommand];
        RecipientFirstCommand = DetailPageCommand(true, AlertPageMove.Latest);
        RecipientPreviousCommand = DetailPageCommand(true, AlertPageMove.Newer);
        RecipientNextCommand = DetailPageCommand(true, AlertPageMove.Older);
        AttemptFirstCommand = DetailPageCommand(false, AlertPageMove.Latest);
        AttemptPreviousCommand = DetailPageCommand(false, AlertPageMove.Newer);
        AttemptNextCommand = DetailPageCommand(false, AlertPageMove.Older);
        OpenRegistrationCommand = new(OpenRegistrationAsync, dispatcher, ActionFailure, CanUseActions);
        CopyIdentifierCommand = new(CopyIdentifierAsync, dispatcher, ActionFailure, CanUseActions);
        selectionCommand = new(ReadSelectionAsync, dispatcher, _ => Report(),
            () => !stopping && State.SelectedEventId is not null && selectionGeneration > settledSelectionGeneration);
        commands = [LoadCommand, ReloadCommand, LatestCommand, NewerCommand, OlderCommand, RecipientFirstCommand,
            RecipientPreviousCommand, RecipientNextCommand, AttemptFirstCommand, AttemptPreviousCommand, AttemptNextCommand,
            OpenRegistrationCommand, CopyIdentifierCommand, selectionCommand];
        foreach (var command in pageCommands) command.CanExecuteChanged += OnPageAvailabilityChanged;
        Model.PropertyChanged += OnStateChanged;
        selectionWorker = RunSelectionRequestsAsync();
    }
    /// <summary>Immutable state holder for bindings.</summary>
    public UiSnapshotModel<AlertHistoryState> Model { get; }
    /// <summary>Current complete rendering state.</summary>
    public AlertHistoryState State => Model.State;
    /// <summary>Dispatcher-published property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Current visible selection; setters resolve identity against the current row set.</summary>
    public AlertHistoryRow? SelectedRow
    {
        get => State.Rows.FirstOrDefault(row => row.EventId == State.SelectedEventId);
        set
        {
            if (stopping || value?.EventId == State.SelectedEventId) return;
            if (value is not null && !State.Rows.Any(row => row.EventId == value.EventId)) return;
            RequestSelection(value?.EventId);
        }
    }
    /// <summary>Reads one initial global page only when not loaded.</summary>
    public UiAsyncCommand LoadCommand { get; }
    /// <summary>Reloads the current global cursor.</summary>
    public UiAsyncCommand ReloadCommand { get; }
    /// <summary>Starts a new latest view/high water and clears backward history.</summary>
    public UiAsyncCommand LatestCommand { get; }
    /// <summary>Reads a retained newer cursor.</summary>
    public UiAsyncCommand NewerCommand { get; }
    /// <summary>Reads the exact older continuation with bounded backward memory.</summary>
    public UiAsyncCommand OlderCommand { get; }
    /// <summary>Starts the selected event's original recipient associations.</summary>
    public UiAsyncCommand RecipientFirstCommand { get; }
    /// <summary>Reads a retained previous recipient page.</summary>
    public UiAsyncCommand RecipientPreviousCommand { get; }
    /// <summary>Reads the exact next recipient continuation.</summary>
    public UiAsyncCommand RecipientNextCommand { get; }
    /// <summary>Starts associated batch-context/event-result record buckets.</summary>
    public UiAsyncCommand AttemptFirstCommand { get; }
    /// <summary>Reads a retained previous record page.</summary>
    public UiAsyncCommand AttemptPreviousCommand { get; }
    /// <summary>Reads the exact next record continuation.</summary>
    public UiAsyncCommand AttemptNextCommand { get; }
    /// <summary>Opens only the fixed official registration homepage.</summary>
    public UiAsyncCommand OpenRegistrationCommand { get; }
    /// <summary>Copies the literal selected course code and section.</summary>
    public UiAsyncCommand CopyIdentifierCommand { get; }
    /// <summary>Selects a current visible identity; null clears selection. Caller cancellation detaches only its waiter.</summary>
    public async Task<bool> SelectEventAsync(Guid? eventId, CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("An event identity is required.", nameof(eventId));
        Task<bool>? selected = null;
        await dispatcher.InvokeAsync(() => selected = RequestSelection(eventId), cancellationToken).ConfigureAwait(false);
        return await selected!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Stops admission and drains all actual reads, callbacks and fixed desktop actions.</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
        {
            if (disposal is not null) return new(disposal);
            stopping = true;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<Task> drain = async () =>
            {
                await ready.Task.ConfigureAwait(false);
                await DrainAsync().ConfigureAwait(false);
            };
            // Retain the one drain before any observable cancellation callback can reenter Dispose.
            // A reentrant UI observer must not inherit a command's self-disposal ownership marker.
            if (ExecutionContext.IsFlowSuppressed()) disposal = Task.Run(drain);
            else { using (ExecutionContext.SuppressFlow()) disposal = Task.Run(drain); }
            selectionRequests.Writer.TryComplete();
            Volatile.Read(ref selectionReady)?.TrySetCanceled(new CancellationToken(true));
            try { foreach (var command in commands) command.Cancel(); }
            finally { ready.TrySetResult(); }
            return new(disposal);
        }
    }

    private async Task DrainAsync()
    {
        await Task.WhenAll(commands.Select(command => command.DisposeAsync().AsTask()).Append(selectionWorker)).ConfigureAwait(false);
        Model.PropertyChanged -= OnStateChanged;
        foreach (var command in pageCommands) command.CanExecuteChanged -= OnPageAvailabilityChanged;
        pageGate.Dispose(); recipientGate.Dispose(); attemptGate.Dispose();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var property in new[] { nameof(State), nameof(SelectedRow) })
            foreach (var observer in PropertyChanged?.GetInvocationList() ?? [])
                try { ((PropertyChangedEventHandler)observer)(this, new(property)); } catch (Exception) { Report(); }
    }

    private void OnPageAvailabilityChanged(object? sender, EventArgs args)
    {
        if (stopping) return;
        var busy = pageCommands.Any(command => command.IsExecuting);
        if (State.IsLoading != busy) Model.Replace(State with { IsLoading = busy });
    }

    private void NotifyCommands() { foreach (var command in commands) command.NotifyCanExecuteChanged(); }
    private void Report()
    {
        try { reportError?.Invoke("ui_alert_history_failed"); }
        catch (Exception) { System.Diagnostics.Trace.WriteLine("ui_alert_history_failed"); }
    }
    private void Failure(string code)
    {
        Report();
        if (!stopping && dispatcher.CheckAccess()) Model.Replace(State with { MessageKey = "Ui.AlertHistoryUnavailable", IsStale = State.IsLoaded });
    }
    private bool CanUseActions() => !stopping && actions is not null && !State.SelectionPending
        && State.Details is { } details && details.EventId == State.SelectedEventId;
    private async Task<SectionKey?> CaptureActionKeyAsync(CancellationToken token)
    {
        SectionKey? key = null;
        await dispatcher.InvokeAsync(() =>
        {
            if (CanUseActions()) key = State.Details!.Source.Trigger.Key;
        }, token).ConfigureAwait(false);
        return key;
    }
    private async Task OpenRegistrationAsync(CancellationToken token)
    {
        if (await CaptureActionKeyAsync(token).ConfigureAwait(false) is null) return;
        token.ThrowIfCancellationRequested();
        await actions!.OpenDestinationAsync(StudentDestination.Registration, token).ConfigureAwait(false);
    }
    private async Task CopyIdentifierAsync(CancellationToken token)
    {
        if (await CaptureActionKeyAsync(token).ConfigureAwait(false) is not { } key) return;
        token.ThrowIfCancellationRequested();
        await actions!.CopySectionIdentifierAsync(key, token).ConfigureAwait(false);
    }
    private void ActionFailure(string code)
    {
        Report();
        if (!stopping && dispatcher.CheckAccess()) Model.Replace(State with { MessageKey = "Ui.CommandFailed" });
    }
}
