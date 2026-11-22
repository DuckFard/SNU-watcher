using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32;
using SnuWatcher.Windows.Hosting;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>Coalesces OS hints into one retained worker; it never performs a source request or owns the host.</summary>
internal sealed class SystemStateBridge(WindowsMonitoringHost host, Action<string> reportError) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly WindowsMonitoringHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly Action<string> _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private bool? _online, _suspended;
    private bool _started, _networkSubscribed, _powerSubscribed;
    private volatile bool _closing;
    private Task _worker = Task.CompletedTask;
    private Task? _disposal;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_started) return;
            _started = true;
        }
        try
        {
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged; _networkSubscribed = true;
            SystemEvents.PowerModeChanged += PowerChanged; _powerSubscribed = true;
            Publish(NetworkInterface.GetIsNetworkAvailable(), false);
            _worker = RunAsync();
        }
        catch (Exception) { _reportError("desktop_system_state_failed"); throw new InvalidOperationException("System-state observation could not start."); }
    }

    private void NetworkChanged(object? sender, NetworkAvailabilityEventArgs args) => Publish(args.IsAvailable, null);
    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend) Publish(null, true);
        else if (args.Mode == PowerModes.Resume) Publish(null, false);
    }

    private void Publish(bool? online, bool? suspended)
    {
        lock (_sync)
        {
            if (_closing) return;
            if (online is not null) _online = online;
            if (suspended is not null) _suspended = suspended;
            _wake.Writer.TryWrite(0);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var ignored in _wake.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                bool? online, suspended;
                lock (_sync)
                {
                    if (_closing) return;
                    online = _online; suspended = _suspended; _online = null; _suspended = null;
                    while (_wake.Reader.TryRead(out _)) { }
                }
                if (online is { } available) await _host.Session.SetOnlineAsync(available, _stop.Token).ConfigureAwait(false);
                if (suspended is { } asleep) await _host.Session.SetSuspendedAsync(asleep, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closing && _host.State == WindowsMonitoringHostState.Running) _reportError("desktop_system_state_failed");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposal is null)
            {
                _closing = true;
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = DisposeCoreAsync(ready.Task);
                ready.SetResult();
            }
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Task ready)
    {
        await ready.ConfigureAwait(false);
        var unsubscribeFailed = false;
        try { if (_networkSubscribed) NetworkChange.NetworkAvailabilityChanged -= NetworkChanged; }
        catch (Exception) { unsubscribeFailed = true; }
        try { if (_powerSubscribed) SystemEvents.PowerModeChanged -= PowerChanged; }
        catch (Exception) { unsubscribeFailed = true; }
        _wake.Writer.TryComplete();
        var cancellation = _stop.CancelAsync();
        try { await Task.WhenAll(_worker, cancellation).ConfigureAwait(false); }
        finally { _stop.Dispose(); }
        if (unsubscribeFailed) throw new InvalidOperationException("System-state observation could not finish stopping.");
    }
}
