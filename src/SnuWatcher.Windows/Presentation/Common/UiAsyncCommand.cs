using System.ComponentModel;
using System.Windows.Input;

namespace SnuWatcher.Windows.Presentation.Common;

/// <summary>Owns one asynchronous operation, cancellation callbacks and final UI publication.</summary>
public sealed partial class UiAsyncCommand : ICommand, INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly AsyncLocal<UiAsyncCommand?> InsideOperation = new();
    private readonly object sync = new();
    private readonly Func<CancellationToken, Task> execute;
    private readonly IUiDispatcher dispatcher;
    private readonly Action<string> reportError;
    private readonly Func<bool>? canExecute;
    private Execution? current;
    private Task<UiCommandOutcome> lastRun = Task.FromResult(UiCommandOutcome.Unavailable);
    private Task notifications = Task.CompletedTask;
    private Task? disposal;
    private bool closed;
    private bool notificationPosted;
    private bool pendingNotificationError;

    /// <summary>Creates a command. Operation callbacks run off the dispatcher; all UI mutation must be dispatched.</summary>
    public UiAsyncCommand(Func<CancellationToken, Task> execute, IUiDispatcher dispatcher, Action<string> reportError,
        Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(reportError);
        this.execute = execute;
        this.dispatcher = dispatcher;
        this.reportError = reportError;
        this.canExecute = canExecute;
    }

    /// <summary>Remains true until the actual driver and owned cancellation callbacks stop.</summary>
    public bool IsExecuting { get { lock (sync) return current?.Running == true; } }

    /// <summary>Whether cancellation was requested for the current or most recent execution.</summary>
    public bool IsCancellationRequested { get { lock (sync) return current?.CancellationRequested == true; } }

    /// <summary>Dispatcher-published changes to command lifetime state.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Dispatcher-published execution availability changes.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Whether a new execution may be admitted. The optional command parameter is unused.</summary>
    public bool CanExecute(object? parameter)
    {
        lock (sync) if (closed || current?.Running == true) return false;
        return EvaluatePredicate();
    }

    /// <summary>Starts an owned execution whose failure is observed even when WPF does not await it.</summary>
    public void Execute(object? parameter) => _ = ExecuteAsync();

    /// <summary>Admits at most one operation and returns its actual command-boundary outcome.</summary>
    public Task<UiCommandOutcome> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(UiCommandOutcome.Canceled);
        lock (sync)
        {
            if (closed) return Task.FromResult(UiCommandOutcome.Unavailable);
            if (current?.Running == true) return Task.FromResult(UiCommandOutcome.Busy);
        }
        if (!EvaluatePredicate()) return Task.FromResult(UiCommandOutcome.Unavailable);
        lock (sync)
        {
            if (closed) return Task.FromResult(UiCommandOutcome.Unavailable);
            if (current?.Running == true) return Task.FromResult(UiCommandOutcome.Busy);
            var state = new Execution(cancellationToken);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = RunAsync(ready.Task, lastRun, state);
            current = state;
            lastRun = running;
            ready.SetResult();
            return running;
        }
    }

    /// <summary>Requests cancellation without synchronously running cancellation callbacks on the UI thread.</summary>
    public void Cancel()
    {
        Execution? state;
        lock (sync) state = current;
        if (state is not null) RequestCancellation(state);
    }

    /// <summary>Coalesces a dispatcher notification after external predicate state changes.</summary>
    public void NotifyCanExecuteChanged() => QueueNotification(reportFailure: false);

    /// <summary>Closes admission and drains the actual operation, cancellation callbacks and UI publication.</summary>
    public ValueTask DisposeAsync()
    {
        if (ReferenceEquals(InsideOperation.Value, this))
            throw new InvalidOperationException("A command cannot await its own disposal.");
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = DisposeCoreAsync(ready.Task, lastRun, current);
            ready.SetResult();
            return new(disposal);
        }
    }

    private bool EvaluatePredicate()
    {
        try { return canExecute?.Invoke() ?? true; }
        catch (Exception) { QueueNotification(reportFailure: true); return false; }
    }

    private void CloseAdmission()
    {
        lock (sync) closed = true;
    }

    private sealed class Execution(CancellationToken caller)
    {
        internal CancellationToken Caller { get; } = caller;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal bool Running { get; set; } = true;
        internal bool AcceptCancellation { get; set; } = true;
        internal bool CancellationRequested { get; set; }
        internal Task CancellationNotifications { get; set; } = Task.CompletedTask;
        internal Task<bool> CancellationPublication { get; set; } = Task.FromResult(true);
    }
}
