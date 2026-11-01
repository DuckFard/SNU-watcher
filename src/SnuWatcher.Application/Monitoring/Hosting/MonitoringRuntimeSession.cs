using System.Threading.Channels;
using System.Collections.Immutable;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Application.Watches;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Hosting;

/// <summary>
/// One generic runtime lifetime. Store capabilities must represent the same logical store. Caller
/// owns a single-permit lifecycle gate, the database owner lease, concrete source/gateway and store.
/// The caller holds that lease and establishes no live senders before StartAsync recovery. This
/// session owns logical actors/commands only; it never closes injected external resources.
/// </summary>
public sealed partial class MonitoringRuntimeSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly RuntimeSessionOptions _options;
    private readonly RuntimeUpdateHub _hub;
    private readonly WatchMonitorLoop _monitor;
    private readonly WatchManagementService _watches;
    private readonly BatchDeliveryCoordinator _coordinator;
    private readonly DeliveryStartupRecovery _recovery;
    private readonly OwnedRefreshBridge _refresh;
    private readonly BatchDeliveryPump _pump;
    private readonly CancellationTokenSource _startupStop = new(), _monitorStop = new(), _pumpStop = new(), _refreshStop = new(), _refreshWaiters = new();
    private readonly SemaphoreSlim _finalization = new(1, 1);
    private ImmutableDictionary<string, Task> _actors = ImmutableDictionary<string, Task>.Empty;
    private ImmutableDictionary<Guid, Task> _commands = ImmutableDictionary<Guid, Task>.Empty;
    private ImmutableArray<Task> _cancellations = [];
    private RuntimeSessionState _state;
    private Task? _startTask, _supervisor;
    private Task<RuntimeSessionStopResult>? _stopCore, _stopPublic, _retry;
    private RuntimeSessionStopResult? _lastStop;
    private bool _stopping, _disposed;

    public MonitoringRuntimeSession(IWatchStore watches, ISourcePolicyStore policy, IBatchDeliveryStore batches,
        IPendingDeliveryWorkReader work, IDeliveryRecoveryStore recovery, ICourseSource source,
        IBatchDesktopAlertChannel channel, TimeProvider clock, RuntimeSessionOptions options,
        SemaphoreSlim lifecycleSendGate, Func<string?>? sourceDiagnosticReader = null,
        RuntimePresentationServices? presentationServices = null)
    {
        ArgumentNullException.ThrowIfNull(watches);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lifecycleSendGate);
        _options = options;
        _interactiveSource = source;
        _presentationServices = presentationServices;
        _hub = new(clock, sourceDiagnosticReader);
        _monitor = new(watches, source, policy, clock, options.Monitor, _hub.Writer);
        _watches = new(watches, lifecycleSendGate);
        _coordinator = new(batches, clock, lifecycleSendGate, channel, maxPendingKnownResults: options.MaxPendingKnownResults);
        _recovery = new(recovery, batches, clock, lifecycleSendGate);
        _refresh = new(_monitor.RefreshAsync, options.MaxRefreshSections);
        _pump = new(work, _coordinator, clock, options.Delivery, key => _refresh.TryRequest(key), options.Pump, OnPumpDiagnostic);
    }

    public RuntimeSessionState State { get { lock (_sync) return _state; } }
    public RuntimeFailure? Failure => _hub.Failure;
    public Task<RuntimeFailure> FailureTask => _hub.FailureTask;
    public WatchMonitorSnapshot? LatestSnapshot => _hub.LatestSnapshot;
    public ChannelReader<RuntimeUpdate> Updates => _hub.Updates;

    /// <summary>The token controls startup only. Successful startup has a separate, explicitly stopped lifetime.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_state != RuntimeSessionState.Created || _startTask is not null)
                throw new InvalidOperationException("A runtime session has one start lifetime.");
            _state = RuntimeSessionState.Starting;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _startTask = StartCoreAsync(ready.Task, cancellationToken);
            ready.SetResult();
            return _startTask;
        }
    }

    private async Task StartCoreAsync(Task ready, CancellationToken caller)
    {
        await ready.ConfigureAwait(false);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(caller, _startupStop.Token);
        try
        {
            await _recovery.RecoverAsync(_options.RecoveryPageSize, startup.Token).ConfigureAwait(false);
            startup.Token.ThrowIfCancellationRequested();
            _hub.CheckSourceDiagnostic();
            if (_hub.Failure is not null) throw new InvalidOperationException();
            lock (_sync)
            {
                if (_stopping) throw new OperationCanceledException(startup.Token);
                startup.Token.ThrowIfCancellationRequested();
                _actors = _actors.Add("monitor", _monitor.RunAsync(_monitorStop.Token));
                _actors = _actors.Add("refresh", _refresh.RunAsync(_refreshStop.Token));
                _actors = _actors.Add("updates", _hub.RunAsync(WakeDelivery, CancellationToken.None));
                if (_options.DeliveryEnabled) _actors = _actors.Add("delivery", _pump.RunAsync(_pumpStop.Token));
                if (!_stopping) _state = RuntimeSessionState.Running;
                _supervisor = SuperviseAsync();
            }
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested || Volatile.Read(ref _stopping))
        {
            lock (_sync) _stopCore = EnsureStopCoreLocked();
            throw;
        }
        catch (Exception)
        {
            _hub.ReportFailure(RuntimeFailureKind.SavedState, "runtime_startup_failed");
            lock (_sync) _stopCore = EnsureStopCoreLocked();
            throw new InvalidOperationException("Runtime startup failed. Monitoring has not started.");
        }
    }

    /// <summary>Coalesced replan only; use after an externally owned source control has durably completed.</summary>
    public bool Wake()
    {
        lock (_sync)
        {
            if (_state != RuntimeSessionState.Running || _stopping) return false;
            return WakeActors();
        }
    }

    private bool WakeActors()
    {
        _hub.CheckSourceDiagnostic();
        if (_hub.Failure is not null)
        {
            lock (_sync) _stopCore = EnsureStopCoreLocked();
            return false;
        }
        _monitor.Wake(); WakeDelivery(false);
        return true;
    }

    private void WakeDelivery(bool sourceChanged)
    {
        if (_options.DeliveryEnabled) _pump.Wake(sourceChanged);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Task<RuntimeSessionStopResult>? retry;
        lock (_sync) retry = _retry;
        if (retry is not null) await retry.ConfigureAwait(false);
        lock (_sync)
        {
            if (_lastStop is not { IsFinalized: true })
                throw new InvalidOperationException("Runtime shutdown is blocked by unsaved alert results. Retain this session and its external resources.");
            if (_disposed) return;
            _disposed = true;
        }
        _startupStop.Dispose(); _monitorStop.Dispose(); _pumpStop.Dispose(); _refreshStop.Dispose(); _refreshWaiters.Dispose();
        _finalization.Dispose();
    }
}
