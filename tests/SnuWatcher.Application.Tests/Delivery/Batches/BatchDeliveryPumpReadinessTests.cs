using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

public sealed class BatchDeliveryPumpReadinessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisabledWorkWaitsThirtyMonotonicSecondsWithoutAllocatingAttemptsAndThenRechecks()
    {
        using var f = new CoordinatorFixture();
        var clock = new PumpTestClock();
        var ready = false;
        f.Channel.Readiness = _ => Task.FromResult(ready ? BatchChannelReadiness.Ready : BatchChannelReadiness.Disabled);
        var sent = Signal();
        f.Channel.Send = (_, _) => { sent.TrySetResult(); return Task.FromResult(BatchChannelOutcome.OsAccepted); };
        var pump = new BatchDeliveryPump(new OneSectionReader(), new(f.Port, clock, f.Gate, f.Channel), clock,
            BatchFixture.Policy(), _ => { });
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        try
        {
            await clock.WaitForArmedTimerAsync();
            Assert.Equal(1, f.Channel.ReadinessReads);
            Assert.Empty(f.Port.Reservations);
            clock.Advance(TimeSpan.FromSeconds(29));
            await clock.WaitForArmedTimerAsync();
            Assert.Equal(1, f.Channel.ReadinessReads);
            ready = true;
            clock.Advance(TimeSpan.FromSeconds(1));
            await sent.Task.WaitAsync(Timeout);
        }
        finally { stop.Cancel(); await run.WaitAsync(Timeout); }
        Assert.Single(f.Port.Reservations);
        Assert.Single(f.Channel.Submissions);
    }

    [Fact]
    public async Task CoalescedChannelWakeRetainsEvidenceAndClearsOnlyNativeDeferralWithoutWaitingForTime()
    {
        using var f = new CoordinatorFixture();
        var clock = new PumpTestClock();
        var firstWake = false; var coalescedWake = true;
        var sent = Signal();
        BatchDeliveryPump pump = null!;
        f.Channel.Readiness = _ =>
        {
            if (f.Channel.ReadinessReads == 1)
            {
                firstWake = pump.Wake();
                coalescedWake = pump.Wake(channelChanged: true);
                return Task.FromResult(BatchChannelReadiness.Disabled);
            }
            return Task.FromResult(BatchChannelReadiness.Ready);
        };
        f.Channel.Send = (_, _) => { sent.TrySetResult(); return Task.FromResult(BatchChannelOutcome.OsAccepted); };
        pump = new(new OneSectionReader(), new(f.Port, clock, f.Gate, f.Channel), clock, BatchFixture.Policy(), _ => { });
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        try { await sent.Task.WaitAsync(Timeout); }
        finally { stop.Cancel(); await run.WaitAsync(Timeout); }
        Assert.True(firstWake);
        Assert.False(coalescedWake);
        Assert.Equal(2, f.Channel.ReadinessReads);
        Assert.Equal(BatchFixture.Now, clock.GetUtcNow());
        Assert.False(pump.Wake(channelChanged: true));
    }

    [Theory]
    [InlineData(BatchNoSubmissionReason.ChannelNotReady)]
    [InlineData(BatchNoSubmissionReason.PayloadRejected)]
    public async Task PostSealZeroCostProofKeepsNativeDeferralAcrossCanonicalResultFlush(BatchNoSubmissionReason reason)
    {
        using var f = new CoordinatorFixture();
        var clock = new PumpTestClock();
        var permitWrites = false;
        f.Port.Record = (command, _) => permitWrites ? Task.FromResult(BatchPort.Receipt(command)) : throw new IOException("private write");
        f.Channel.SendResult = (_, _) => Task.FromResult(new BatchChannelResult(BatchChannelOutcome.NotSubmitted, reason));
        var coordinator = new BatchDeliveryCoordinator(f.Port, clock, f.Gate, f.Channel);
        var flushed = Signal();
        var pump = new BatchDeliveryPump(new OneSectionReader(), coordinator, clock, BatchFixture.Policy(), _ => { },
            observer: (_, code) => { if (code == "known_result_recorded") flushed.TrySetResult(); });
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        try
        {
            await clock.WaitForArmedTimerAsync();
            var original = Assert.Single(f.Port.Results);
            permitWrites = true;
            clock.Advance(TimeSpan.FromSeconds(5));
            await flushed.Task.WaitAsync(Timeout);
            await clock.WaitForArmedTimerAsync();
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.WaitForArmedTimerAsync();
            Assert.Single(f.Port.Reservations);
            Assert.Single(f.Channel.Submissions);
            Assert.All(f.Port.Results, command => Assert.Same(original, command));
        }
        finally { stop.Cancel(); await run.WaitAsync(Timeout); }
    }

    [Fact]
    public async Task SourceAndChannelGenerationsCannotClearTheOtherSectionsDistinctDeferral()
    {
        using var f = new CoordinatorFixture();
        var clock = new PumpTestClock();
        var freshKey = BatchFixture.Key();
        var nativeKey = BatchFixture.Key("013");
        var current = freshKey;
        f.Port.Project = (command, _) =>
        {
            current = command.Key;
            var batch = new DeliveryBatchSnapshot(current == freshKey ? BatchFixture.BatchId : BatchFixture.SecondId,
                current, 1, DeliveryBatchStatus.Open, 2, 2, true, BatchFixture.Now);
            return Task.FromResult(new BatchProjection(BatchProjectionStatus.ReadyToEvaluate, batch, null));
        };
        f.Port.Read = (at, _) =>
        {
            var batch = new DeliveryBatchSnapshot(current == freshKey ? BatchFixture.BatchId : BatchFixture.SecondId,
                current, 1, DeliveryBatchStatus.Open, 2, 2, true, BatchFixture.Now);
            var baseline = new SeatBaseline(current, new(119, 120), QuotaMeaning.EnrolledOverQuota, 3, BatchFixture.Now);
            var state = new SectionSnapshot(current, 1, 3,
                current == freshKey ? CheckOutcome.Unavailable : CheckOutcome.Success, at, at, baseline);
            return Task.FromResult(BatchFixture.Context(batch: batch, section: state, evaluatedAt: at,
                subscriptions: [BatchFixture.Watch(key: current)]));
        };
        f.Channel.Readiness = _ => Task.FromResult(BatchChannelReadiness.Disabled);
        var requests = 0;
        var pump = new BatchDeliveryPump(new TwoSectionReader(freshKey, nativeKey), new(f.Port, clock, f.Gate, f.Channel),
            clock, BatchFixture.Policy(), _ => requests++);
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        try
        {
            await clock.WaitForArmedTimerAsync();
            Assert.Equal(1, requests);
            Assert.Equal(1, f.Channel.ReadinessReads);
            pump.Wake(channelChanged: true);
            await clock.WaitForArmedTimerAsync();
            Assert.Equal(1, requests);
            Assert.Equal(2, f.Channel.ReadinessReads);
            pump.Wake(sourceChanged: true);
            await clock.WaitForArmedTimerAsync();
            Assert.Equal(2, requests);
            Assert.Equal(2, f.Channel.ReadinessReads);
        }
        finally { stop.Cancel(); await run.WaitAsync(Timeout); }
        Assert.Empty(f.Port.Reservations);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class OneSectionReader : IPendingDeliveryWorkReader
    {
        public Task<PendingDeliveryWorkPage> ReadPageAsync(PendingDeliveryWorkCursor? after, int limit, CancellationToken token) =>
            Task.FromResult(new PendingDeliveryWorkPage([new SectionDeliveryWork(BatchFixture.Key(), 1, null, false)], null, false));
    }

    private sealed class TwoSectionReader(SectionKey first, SectionKey second) : IPendingDeliveryWorkReader
    {
        public Task<PendingDeliveryWorkPage> ReadPageAsync(PendingDeliveryWorkCursor? after, int limit, CancellationToken token) =>
            Task.FromResult(new PendingDeliveryWorkPage([new(first, 1, null, false), new(second, 2, null, false)], null, false));
    }

    private sealed class PumpTestClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(BatchFixture.Now);
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            _timers.Writer.TryWrite(dueTime); // The fake timer is actually armed before tests may advance it.
            return timer;
        }
        public void Advance(TimeSpan amount) => _clock.Advance(amount);
        public Task<TimeSpan> WaitForArmedTimerAsync() => _timers.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
    }
}
