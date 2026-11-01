namespace SnuWatcher.Windows.Presentation.Common;

/// <summary>One serialized UI queue. Posted actions execute in admission order.</summary>
public interface IUiDispatcher
{
    /// <summary>Whether the caller is already on this dispatcher's UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues an action without blocking the caller. Callers must contain action exceptions.</summary>
    void Post(Action action);

    /// <summary>
    /// Invokes an action on the UI thread. The returned task represents actual action completion;
    /// cancellation may skip an action before it starts, but cannot pretend a running action ended.
    /// </summary>
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
