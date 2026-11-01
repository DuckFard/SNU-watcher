using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Sources;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Infrastructure.Snu;
using SnuWatcher.Infrastructure.Snu.Gateway;
using System.Threading.Channels;

namespace SnuWatcher.Windows.Hosting;

/// <summary>Concrete per-user resource owner. No WPF, notification registration or production channel is created here.</summary>
public sealed partial class WindowsMonitoringHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SqliteWatchStore _store;
    private readonly WindowsMonitoringHostSource _source;
    private readonly DatabaseOwnerGate _owner;
    private readonly SemaphoreSlim _gate;
    private readonly MonitoringRuntimeSession _session;
    private readonly Channel<SourceControl> _controls;
    private readonly TaskCompletionSource _stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _sourceFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _controlsWorker = Task.CompletedTask;
    private Task _failureObserver = Task.CompletedTask;
    private Task _stopWorker = Task.CompletedTask;
    private Task<WindowsHostStopResult>? _stopTask;
    private Task<WindowsHostStopResult>? _retryTask;
    private RuntimeSessionStopResult? _logicalStop;
    private bool _closing, _gatewayClosed, _sourceClosed, _storeClosed, _gateClosed, _ownerClosed;
    private int _state = (int)WindowsMonitoringHostState.Starting;
    private int _controlCount;
    private readonly int _maxControls;

    private WindowsMonitoringHost(RuntimeProfile profile, SqliteWatchStore store, WindowsMonitoringHostSource source,
        DatabaseOwnerGate owner, SemaphoreSlim gate, IBatchDesktopAlertChannel channel, TimeProvider clock, RuntimeSessionOptions options)
    {
        _store = store; _source = source; _owner = owner; _gate = gate;
        _maxControls = options.MaxAdmittedCommands;
        _controls = Channel.CreateBounded<SourceControl>(new BoundedChannelOptions(_maxControls)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        _session = new(store, store, store, store, store, source.Courses, channel, clock, options, gate, source.Diagnostic);
    }
    /// <summary>Opens only the fixed normal profile and mandatory anonymous store-backed source; never called by default M4 launch.</summary>
    public static Task<WindowsHostOpenResult> OpenProductionAsync(IBatchDesktopAlertChannel channel, TimeProvider? clock = null,
        RuntimeSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return OpenCoreAsync(RuntimeProfile.Production(), channel, new(OpenStoreAsync, OpenProductionSourceAsync),
            clock ?? TimeProvider.System, options ?? new(), cancellationToken);
    }

    /// <summary>Opens a marked private run; source ownership transfers only after this process obtains its owner lease.</summary>
    public static Task<WindowsHostOpenResult> OpenValidationAsync(RuntimeProfile profile, Func<ICourseSource> sourceFactory,
        IBatchDesktopAlertChannel channel, TimeProvider? clock = null, RuntimeSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(sourceFactory); ArgumentNullException.ThrowIfNull(channel);
        if (!profile.IsValidation) throw new ArgumentException("A marked private validation profile is required.", nameof(profile));
        return OpenCoreAsync(profile, channel, new(OpenStoreAsync, (_, _, _) =>
        {
            var source = sourceFactory() ?? throw new InvalidOperationException("The validation source factory returned no source.");
            return Task.FromResult(WindowsMonitoringHostSource.FromFake(source));
        }), clock ?? TimeProvider.System, options ?? new(), cancellationToken);
    }

    internal static Task<WindowsHostOpenResult> OpenForTestingAsync(RuntimeProfile profile, IBatchDesktopAlertChannel channel,
        WindowsMonitoringHostFactories factories, TimeProvider? clock = null, RuntimeSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(channel); ArgumentNullException.ThrowIfNull(factories);
        if (!profile.IsValidation) throw new ArgumentException("Tests cannot select the normal profile.", nameof(profile));
        return OpenCoreAsync(profile, channel, factories, clock ?? TimeProvider.System, options ?? new(), cancellationToken);
    }

    /// <summary>The generic logical session; all watch mutations must use its admitted commands.</summary>
    public MonitoringRuntimeSession Session => _session;
    /// <summary>Physical host state, distinct from the logical session's stopped state.</summary>
    public WindowsMonitoringHostState State => (WindowsMonitoringHostState)Volatile.Read(ref _state);
    /// <summary>Stops admission immediately, then awaits actual work and resource closure; blocked writes retain ownership.</summary>
    public Task<WindowsHostStopResult> StopAsync() => BeginStop(retry: false, CancellationToken.None);
    /// <summary>Retries only retained canonical finalization; never restarts actors or startup recovery.</summary>
    public Task<WindowsHostStopResult> RetryFinalizeStopAsync(CancellationToken cancellationToken = default) => BeginStop(retry: true, cancellationToken);
    /// <summary>Caller cancellation detaches its waiter; the host retains and drains the actual source control.</summary>
    public async Task<SourceAccessResumeResult> ResumeSourceAccessAsync(long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return await QueueControlAsync(false, expectedRevision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The source resume control returned no policy result.");
    }
    /// <summary>Explicit policy recovery is owned through completion even if its UI waiter closes.</summary>
    public async Task RecoverSourcePolicyAsync(CancellationToken cancellationToken = default) =>
        _ = await QueueControlAsync(true, 0, cancellationToken).ConfigureAwait(false);
    /// <summary>Throws instead of claiming disposal when canonical finalization remains blocked.</summary>
    public async ValueTask DisposeAsync()
    {
        var result = await StopAsync().ConfigureAwait(false);
        if (!result.IsFinalized) throw new InvalidOperationException("Runtime finalization is blocked; canonical results and ownership were retained.");
        await _stopWorker.ConfigureAwait(false);
        await _failureObserver.ConfigureAwait(false);
    }

    private static Task<SqliteWatchStore> OpenStoreAsync(RuntimeProfile profile, TimeProvider clock, CancellationToken ct) =>
        SqliteWatchStore.OpenAsync(new(profile.DatabasePath), clock, cancellationToken: ct);

    private static async Task<WindowsMonitoringHostSource> OpenProductionSourceAsync(SqliteWatchStore store, TimeProvider clock, CancellationToken ct)
    {
        var source = await SnuCourseSource.CreateAsync(store, timeProvider: clock, cancellationToken: ct).ConfigureAwait(false);
        try
        {
            var gateway = new SnuCourseGateway(source, clock);
            return new(gateway, gateway.DisposeAsync, source.DisposeAsync, () => source.SourcePolicyDiagnosticCode,
                token => source.RecoverSourcePolicyAsync(token), (revision, token) => source.ResumeSourceAccessAsync(revision, token));
        }
        catch { await source.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task<WindowsHostOpenResult> OpenCoreAsync(RuntimeProfile profile, IBatchDesktopAlertChannel channel,
        WindowsMonitoringHostFactories factories, TimeProvider clock, RuntimeSessionOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (profile.IsValidation) profile.ValidateExisting();
        var acquisition = await DatabaseOwnerGate.TryAcquireAsync(profile.ApplicationId, ct).ConfigureAwait(false);
        if (acquisition.Disposition == DatabaseOwnerDisposition.AlreadyOwned) return new(WindowsHostOpenDisposition.AlreadyOwned, null);
        var owner = acquisition.Lease!;
        SqliteWatchStore? store = null;
        WindowsMonitoringHostSource? source = null;
        SemaphoreSlim? gate = null;
        WindowsMonitoringHost? host = null;
        try
        {
            profile.EnsureDirectoryAfterOwnership();
            store = await factories.OpenStore(profile, clock, ct).ConfigureAwait(false);
            source = await factories.OpenSource(store, clock, ct).ConfigureAwait(false);
            gate = new(1, 1);
            host = new(profile, store, source, owner, gate, channel, clock, options);
            await host.Session.StartAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref host._state, (int)WindowsMonitoringHostState.Running);
            host._controlsWorker = host.RunControlsAsync();
            host._failureObserver = host.ObserveFailuresAsync();
            return new(WindowsHostOpenDisposition.Started, host);
        }
        catch (Exception error)
        {
            if (host is not null)
            {
                var stopped = await host.StopAsync().ConfigureAwait(false);
                if (!stopped.IsFinalized) throw new WindowsMonitoringHostStartupException(host, error);
            }
            else
            {
                try
                {
                    if (source is not null)
                    {
                        try { await source.CloseGateway().ConfigureAwait(false); }
                        finally { await source.CloseSource().ConfigureAwait(false); }
                    }
                }
                finally
                {
                    try { if (store is not null) await store.DisposeAsync().ConfigureAwait(false); }
                    finally { gate?.Dispose(); await owner.DisposeAsync().ConfigureAwait(false); }
                }
            }
            throw;
        }
    }
}
