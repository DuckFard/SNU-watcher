using SnuWatcher.Application.Sources;

namespace SnuWatcher.Infrastructure.Snu.RequestQueue;

/// <summary>One source-wide HTTP lane. It queues operations, never shares responses or creates logical watch attempts.</summary>
internal sealed partial class SnuRequestDispatcher : IDisposable, IAsyncDisposable
{
    private static readonly AsyncLocal<SnuRequestDispatcher?> Executing = new();
    private static readonly TimeSpan MaximumTimerSlice = TimeSpan.FromDays(1);
    private readonly object sync = new();
    private readonly LinkedList<WorkItem> interactive = new();
    private readonly LinkedList<WorkItem> watches = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TimeProvider clock;
    private readonly SourceQueueOptions options;
    private readonly Action<SourceRequestPurpose>? queuedForTesting;
    private readonly bool applyEscapingFailures;
    private readonly Task worker;
    private TaskCompletionSource changed = NewSignal();
    private SourceQueuePolicy policy = new();
    private Task? shutdown;
    private bool closed;
    private bool inFlight;
    private bool hasStarted;
    private long lastStart;
    private DateTimeOffset? lastTransportStartedAtUtc;
    private int interactiveStreak;
    private Exception? storageFailure;

    public SnuRequestDispatcher(TimeProvider clock, SourceQueueOptions options, Action<SourceRequestPurpose>? queuedForTesting = null,
        bool applyEscapingFailures = true)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.queuedForTesting = queuedForTesting;
        this.applyEscapingFailures = applyEscapingFailures;
        worker = Task.Run(RunAsync);
    }

    public Task<T> DispatchAsync<T>(SourceRequestPurpose purpose, Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        Task<T> result;
        lock (sync)
        {
            if (closed) return Task.FromException<T>(new ObjectDisposedException(nameof(SnuRequestDispatcher)));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
            if (storageFailure is not null) return Task.FromException<T>(storageFailure);
            if (policy.AccessPaused) return Task.FromException<T>(Restricted());
            if (interactive.Count + watches.Count >= options.Capacity)
                return Task.FromException<T>(new SnuSourceException(SourceFailureKind.LimitReached, "The source request queue is full."));
            var item = new WorkItem<T>(operation, cancellationToken);
            item.Node = (purpose == SourceRequestPurpose.Watch ? watches : interactive).AddLast(item);
            // Registration and dequeue use the same lock. Inline cancellation is safe because Monitor is reentrant.
            item.Registration = cancellationToken.Register(() => CancelQueued(item));
            Pulse();
            result = item.Completion.Task;
        }
        queuedForTesting?.Invoke(purpose);
        return result;
    }

    internal void ImportStoredGate(bool accessPaused, TimeSpan delay, bool clearAccessPause = false)
    {
        lock (sync)
        {
            if (closed) return;
            policy = policy.Extend(delay, clock) with { AccessPaused = clearAccessPause ? accessPaused : policy.AccessPaused || accessPaused };
            Pulse();
        }
    }

    internal void SetStorageFailure(Exception failure)
    {
        List<WorkItem> pending;
        lock (sync)
        {
            storageFailure = failure;
            pending = DetachPending();
            foreach (var item in pending) item.Fail(failure);
            Pulse();
        }
        foreach (var item in pending) item.Registration.Dispose();
    }

    internal void ClearStorageFailure()
    {
        lock (sync) { if (!closed) { storageFailure = null; Pulse(); } }
    }

    internal void ApplyKnownFailure(SnuSourceException failure) => ApplyFailure(failure);

    public SourceQueueSnapshot GetSnapshot()
    {
        lock (sync) return new(policy.AccessPaused, policy.Remaining(clock), interactive.Count, watches.Count, inFlight)
        { LastTransportStartedAtUtc = lastTransportStartedAtUtc };
    }

    internal void RecordTransportStart()
    {
        lock (sync)
        {
            EnsureTransportCallback();
            lastTransportStartedAtUtc = clock.GetUtcNow().ToUniversalTime();
            lastStart = clock.GetTimestamp();
        }
    }

    internal void CompleteTransportInitiation()
    {
        lock (sync)
        {
            EnsureTransportCallback();
            // A conservative post-invocation floor also covers preemption or synchronous handler work.
            // The actual UTC start fact above remains unchanged.
            lastStart = clock.GetTimestamp();
        }
    }

    private void EnsureTransportCallback()
    {
        if (Executing.Value != this || !inFlight)
            throw new InvalidOperationException("Only the active source callback can record an HTTP start.");
    }

    public void ResumeAccess()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            policy = policy with { AccessPaused = false };
            Pulse();
        }
    }

    /// <summary>Trusted policy input only. No unverified SNU HTML template is classified as maintenance.</summary>
    public void DeclareMaintenance(TimeSpan? retryAfter = null)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            policy = policy.Extend(retryAfter ?? TimeSpan.FromSeconds(120), clock);
            Pulse();
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            WorkItem? item = null;
            Task signal;
            TimeSpan delay;
            lock (sync)
            {
                if (closed) return;
                signal = changed.Task;
                delay = NextDelay();
                if (interactive.Count + watches.Count > 0 && delay == TimeSpan.Zero)
                {
                    item = TakeNext();
                    inFlight = true;
                    hasStarted = true;
                    lastStart = clock.GetTimestamp();
                }
            }
            if (item is null)
            {
                await WaitForChangeAsync(signal, delay).ConfigureAwait(false);
                continue;
            }
            item.Registration.Dispose();
            Executing.Value = this;
            try { await item.ExecuteAsync(stopping.Token, error => { if (applyEscapingFailures) ApplyFailure(error); }).ConfigureAwait(false); }
            finally
            {
                Executing.Value = null;
                lock (sync) { inFlight = false; Pulse(); }
            }
        }
    }

    private TimeSpan NextDelay()
    {
        if (interactive.Count + watches.Count == 0) return Timeout.InfiniteTimeSpan;
        var spacing = hasStarted ? options.RequestSpacing - clock.GetElapsedTime(lastStart, clock.GetTimestamp()) : TimeSpan.Zero;
        var cooldown = policy.Remaining(clock);
        return cooldown > spacing ? cooldown : spacing > TimeSpan.Zero ? spacing : TimeSpan.Zero;
    }

    private WorkItem TakeNext()
    {
        var useWatch = watches.Count > 0 && (interactive.Count == 0 || interactiveStreak >= 2);
        var lane = useWatch ? watches : interactive;
        var item = lane.First!.Value;
        lane.RemoveFirst();
        item.Node = null;
        interactiveStreak = useWatch ? 0 : Math.Min(2, interactiveStreak + 1);
        return item;
    }

    private async Task WaitForChangeAsync(Task signal, TimeSpan delay)
    {
        if (delay == Timeout.InfiniteTimeSpan) { await signal.ConfigureAwait(false); return; }
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var timer = Task.Delay(delay > MaximumTimerSlice ? MaximumTimerSlice : delay, clock, timerCancellation.Token);
        await Task.WhenAny(signal, timer).ConfigureAwait(false);
        // This races only a timer wait, never the actual operation. Stop its timer before rechecking all policy.
        await timerCancellation.CancelAsync().ConfigureAwait(false);
        try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private void ApplyFailure(SnuSourceException error)
    {
        List<WorkItem> rejected;
        lock (sync)
        {
            policy = policy.Apply(error, clock);
            rejected = policy.AccessPaused ? DetachPending() : [];
            foreach (var pending in rejected) pending.Fail(Restricted());
            Pulse();
        }
        foreach (var pending in rejected) pending.Registration.Dispose();
    }

    private void CancelQueued(WorkItem item)
    {
        lock (sync)
        {
            if (item.Node is null) return;
            item.Node.List!.Remove(item.Node);
            item.Node = null;
            item.Cancel();
            Pulse();
        }
        item.Registration.Unregister();
    }

    private List<WorkItem> DetachPending()
    {
        var pending = interactive.Concat(watches).ToList();
        interactive.Clear(); watches.Clear();
        foreach (var item in pending) item.Node = null;
        return pending;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var signal = changed; changed = NewSignal(); signal.TrySetResult(); }
    private static SnuSourceException Restricted() => new(SourceFailureKind.AccessRestricted, "SNU access is paused. Explicitly resume after reviewing access restrictions.");

    public ValueTask DisposeAsync()
    {
        EnsureCanDispose();
        TaskCompletionSource completion;
        List<WorkItem> pending;
        lock (sync)
        {
            if (shutdown is not null) return new(shutdown);
            closed = true;
            pending = DetachPending();
            foreach (var item in pending) item.Cancel();
            completion = NewSignal();
            shutdown = completion.Task;
            Pulse();
        }
        foreach (var item in pending) item.Registration.Dispose();
        _ = CompleteShutdownAsync(completion);
        return new(shutdown);
    }

    internal void EnsureCanDispose()
    {
        if (Executing.Value == this) throw new InvalidOperationException("A source callback cannot dispose its own dispatcher.");
    }

    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        Exception? cancellationFailure = null;
        try { stopping.Cancel(); } catch (AggregateException error) { cancellationFailure = error; }
        try
        {
            await worker.ConfigureAwait(false);
            stopping.Dispose();
            if (cancellationFailure is not null) completion.TrySetException(cancellationFailure);
            else completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
