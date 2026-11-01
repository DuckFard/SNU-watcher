using System.ComponentModel;
using System.Diagnostics;

namespace SnuWatcher.Windows.Presentation.Common;

/// <summary>Publishes replacement immutable snapshots; it never edits a supplied snapshot.</summary>
public sealed class UiSnapshotModel<TSnapshot> : INotifyPropertyChanged where TSnapshot : class
{
    private readonly IUiDispatcher dispatcher;
    private readonly Action<string>? reportError;
    private TSnapshot state;

    /// <summary>Creates a holder whose input snapshots must be immutable.</summary>
    public UiSnapshotModel(TSnapshot initialState, IUiDispatcher dispatcher, Action<string>? reportError = null)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(dispatcher);
        state = initialState;
        this.dispatcher = dispatcher;
        this.reportError = reportError;
    }

    /// <summary>The most recently published complete snapshot.</summary>
    public TSnapshot State => Volatile.Read(ref state);

    /// <summary>Raised on the dispatcher when State is replaced.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Synchronously replaces state on the dispatcher, for binding setters.</summary>
    public void Replace(TSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!dispatcher.CheckAccess())
            throw new InvalidOperationException("UI state can only be replaced on its dispatcher.");
        Publish(snapshot);
    }

    /// <summary>Publishes a new immutable snapshot through the dispatcher.</summary>
    public Task ReplaceAsync(TSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => Replace(snapshot), cancellationToken);
        Replace(snapshot);
        return Task.CompletedTask;
    }

    private void Publish(TSnapshot snapshot)
    {
        if (ReferenceEquals(state, snapshot)) return;
        Volatile.Write(ref state, snapshot);
        foreach (var observer in PropertyChanged?.GetInvocationList() ?? [])
        {
            try { ((PropertyChangedEventHandler)observer)(this, new(nameof(State))); }
            catch (Exception)
            {
                try { reportError?.Invoke("ui_state_notification_failed"); }
                catch (Exception) { Trace.WriteLine("ui_state_notification_failed"); }
                if (reportError is null) Trace.WriteLine("ui_state_notification_failed");
            }
        }
    }
}
