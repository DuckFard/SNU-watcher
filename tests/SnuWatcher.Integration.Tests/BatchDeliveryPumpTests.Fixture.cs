using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Core;
using SnuWatcher.Integration.Tests.Persistence;

namespace SnuWatcher.Integration.Tests;

public sealed partial class BatchDeliveryPumpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly BatchDeliveryPolicy Policy = new(TimeSpan.FromMinutes(2), 3);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PumpClock(StoreClock storeClock) : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(storeClock.GetUtcNow());
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            _timers.Writer.TryWrite(dueTime);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            storeClock.Set(_clock.GetUtcNow().Add(amount));
            _clock.Advance(amount);
        }
        public async Task DriveUntilAsync(Func<bool> done)
        {
            for (var i = 0; i < 64 && !done(); i++)
            {
                var delay = await _timers.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                if (!done()) Advance(delay);
                await Task.Yield();
            }
            if (!done()) throw new TimeoutException("The bounded fake-time pump drive did not reach its observation.");
        }
    }

    private sealed class PumpChannel(PumpClock clock) : IBatchDesktopAlertChannel
    {
        public ConcurrentQueue<(BatchDeliveryEnvelope Envelope, long Started)> Calls { get; } = new();
        public Func<BatchDeliveryEnvelope, CancellationToken, Task<BatchChannelOutcome>> Handler { get; set; } =
            (_, _) => Task.FromResult(BatchChannelOutcome.OsAccepted);
        public Func<CancellationToken, Task<BatchChannelReadiness>> Readiness { get; set; } = _ => Task.FromResult(BatchChannelReadiness.Ready);
        public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken token) => Readiness(token);
        public async Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken token)
        {
            var envelope = submission.Envelope;
            Calls.Enqueue((envelope, clock.GetTimestamp()));
            return new BatchChannelResult(await Handler(envelope, token));
        }
    }

    private sealed class ResultFaultPort(IBatchDeliveryStore inner, bool failBeforeCommit = false) : IBatchDeliveryStore
    {
        private bool _lost;
        public ConcurrentQueue<BatchAttemptResultCommand> Commands { get; } = new();
        public Task<ImmutableArray<SectionDeliveryWork>> ReadPendingDeliverySectionsAsync(int limit, CancellationToken ct) => inner.ReadPendingDeliverySectionsAsync(limit, ct);
        public Task<BatchProjection> ProjectDeliveryBatchAsync(ProjectDeliveryBatchCommand command, CancellationToken ct) => inner.ProjectDeliveryBatchAsync(command, ct);
        public Task<BatchDeliveryContext> ReadDeliveryBatchContextAsync(Guid id, BatchDeliveryPolicy policy, DateTimeOffset at, CancellationToken ct) => inner.ReadDeliveryBatchContextAsync(id, policy, at, ct);
        public Task<BatchReservationResult> TryReserveBatchAttemptAsync(PreparedBatchAttempt command, CancellationToken ct) => inner.TryReserveBatchAttemptAsync(command, ct);
        public async Task<BatchAttemptReceipt> RecordBatchAttemptResultAsync(BatchAttemptResultCommand command, CancellationToken ct)
        {
            Commands.Enqueue(command);
            if (failBeforeCommit) throw new IOException("Simulated unavailable result persistence.");
            var receipt = await inner.RecordBatchAttemptResultAsync(command, ct);
            if (!_lost) { _lost = true; throw new IOException("Lost completed write return."); }
            return receipt;
        }
        public Task<ImmutableArray<BatchAttemptCoverageItem>> ReadBatchAttemptCoverageAsync(Guid id, long after, int limit, CancellationToken ct) => inner.ReadBatchAttemptCoverageAsync(id, after, limit, ct);
    }
}
