using System.Threading.Channels;
using SnuWatcher.Application.Monitoring.Scheduling;

namespace SnuWatcher.Application.Monitoring.Hosting;

/// <summary>
/// Retains immutable latest state and sticky commit/failure evidence before bounded wake/render hints.
/// UI backpressure never blocks monitoring. The caller owns and observes the single RunAsync task.
/// </summary>
public sealed class RuntimeUpdateHub
{
    public const int MaxUpdateCapacity = 256;
    private readonly TimeProvider _clock;
    private readonly Func<string?>? _sourceDiagnosticReader;
    private readonly object _sync = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<RuntimeUpdate> _updates;
    private readonly TaskCompletionSource<RuntimeFailure> _failureReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WatchMonitorSnapshot? _latest;
    private RuntimeFailure? _failure;
    private long _generation;
    private int _started;
    private bool _completed;
    private bool _inputFaulted;

    public RuntimeUpdateHub(TimeProvider clock, Func<string?>? sourceDiagnosticReader = null, int updateCapacity = 1)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (updateCapacity is < 1 or > MaxUpdateCapacity) throw new ArgumentOutOfRangeException(nameof(updateCapacity));
        _clock = clock;
        _sourceDiagnosticReader = sourceDiagnosticReader;
        _updates = Channel.CreateBounded<RuntimeUpdate>(new BoundedChannelOptions(updateCapacity)
            { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
        Writer = new UpdateWriter(this);
    }

    public ChannelWriter<WatchMonitorUpdate> Writer { get; }
    public ChannelReader<RuntimeUpdate> Updates => _updates.Reader;
    public WatchMonitorSnapshot? LatestSnapshot => Volatile.Read(ref _latest);
    public long CommittedGeneration { get { lock (_sync) return _generation; } }
    public RuntimeFailure? Failure => Volatile.Read(ref _failure);
    public Task<RuntimeFailure> FailureTask => _failureReady.Task;

    public Task RunAsync(Action<bool> wakeDelivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wakeDelivery);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("The runtime update hub has one run lifetime.");
        return RunCoreAsync(wakeDelivery, cancellationToken);
    }

    private async Task RunCoreAsync(Action<bool> wakeDelivery, CancellationToken token)
    {
        long observedGeneration = 0;
        CheckSourceDiagnostic();
        try
        {
            while (await _wake.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_wake.Reader.TryRead(out _)) { }
                CheckSourceDiagnostic();
                long generation;
                lock (_sync) generation = _generation;
                if (generation != observedGeneration)
                {
                    observedGeneration = generation;
                    lock (_sync)
                    {
                        if (_failure is null)
                        {
                            try { wakeDelivery(true); }
                            catch (Exception) { ReportFailure(RuntimeFailureKind.Actor, "delivery_wake_failed"); throw; }
                        }
                    }
                }
                lock (_sync) PublishUi();
            }
            if (_inputFaulted) throw new InvalidOperationException();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            ReportFailure(RuntimeFailureKind.Actor, "runtime_update_failed");
            throw new InvalidOperationException("The runtime update worker failed.");
        }
        finally
        {
            lock (_sync)
            {
                _completed = true;
                _wake.Writer.TryComplete();
                _updates.Writer.TryComplete();
            }
        }
    }

    public void Complete() => CompleteInput(error: false);

    private bool CompleteInput(bool error)
    {
        if (error) ReportFailure(RuntimeFailureKind.Actor, "monitor_updates_failed");
        lock (_sync)
        {
            if (_completed) return false;
            _completed = true;
            _inputFaulted = error;
            _wake.Writer.TryComplete();
            if (Volatile.Read(ref _started) == 0)
            {
                PublishUi();
                _updates.Writer.TryComplete();
            }
            return true;
        }
    }

    public void CheckSourceDiagnostic()
    {
        try
        {
            if (_sourceDiagnosticReader?.Invoke() == "source_policy_storage_failed")
                ReportFailure(RuntimeFailureKind.SavedState, "source_policy_storage_failed");
        }
        catch (Exception) { ReportFailure(RuntimeFailureKind.SavedState, "source_diagnostic_failed"); }
    }

    public void ReportFailure(RuntimeFailureKind kind, string boundedCode)
    {
        if (!Enum.IsDefined(kind)) kind = RuntimeFailureKind.Actor;
        var code = boundedCode is { Length: > 0 and <= 64 }
            && boundedCode.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-')
                ? boundedCode : "runtime_dependency_failed";
        DateTimeOffset at;
        try { at = _clock.GetUtcNow(); }
        catch (Exception) { at = DateTimeOffset.MinValue; }
        lock (_sync)
        {
            if (_failure is not null) return;
            _failure = new(kind, code, at);
            // Sticky evidence is committed before either bounded hint channel is touched.
            _failureReady.TrySetResult(_failure);
            PublishUi();
            _wake.Writer.TryWrite(0);
        }
    }

    private bool Accept(WatchMonitorUpdate update)
    {
        if (update is null || update.Snapshot is null || update.CommittedSections.IsDefault)
        { ReportFailure(RuntimeFailureKind.Actor, "monitor_update_invalid"); return false; }
        lock (_sync)
        {
            if (_completed) return false;
            _latest = update.Snapshot;
            if (!update.CommittedSections.IsEmpty) _generation = unchecked(_generation + 1);
            _wake.Writer.TryWrite(0);
            return true;
        }
    }

    // Called under _sync; immutable snapshots and only the latest generation are retained.
    private void PublishUi()
    {
        if (_latest is not null || _failure is not null) _updates.Writer.TryWrite(new(_latest, _failure, _generation));
    }

    private sealed class UpdateWriter(RuntimeUpdateHub owner) : ChannelWriter<WatchMonitorUpdate>
    {
        public override bool TryWrite(WatchMonitorUpdate item) => owner.Accept(item);
        public override bool TryComplete(Exception? error = null) => owner.CompleteInput(error is not null);
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<bool>(cancellationToken);
            lock (owner._sync) return ValueTask.FromResult(!owner._completed);
        }
    }
}
