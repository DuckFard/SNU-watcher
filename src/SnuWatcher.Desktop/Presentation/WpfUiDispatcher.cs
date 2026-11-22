using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>Dispatcher adapter for framework-neutral presentation models.</summary>
internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureRunning();
        _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureRunning();
        if (!CheckAccess())
            return _dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, cancellationToken).Task;
        action();
        return Task.CompletedTask;
    }

    private void EnsureRunning()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("The interface dispatcher is stopped.");
    }
}
