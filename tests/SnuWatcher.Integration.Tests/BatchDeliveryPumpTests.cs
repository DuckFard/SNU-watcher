using System.Collections.Concurrent;
using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Integration.Tests.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed partial class BatchDeliveryPumpTests
{
    [Fact]
    public async Task EarlyUnknownPagesRequestFreshWithoutBlockingLaterFreshSectionsAndNativeCallsStaySpaced()
    {
        await using var f = await StoreFixture.CreateAsync();
        var keys = Enumerable.Range(1, 8).Select(number => StoreFixture.Key(number.ToString("000"))).ToArray();
        foreach (var key in keys)
        {
            await f.AddAsync(key);
            await f.ObserveAsync(key, 119);
            if (key != keys[^1] && key != keys[^2]) await f.CommitAsync(f.Unknown(await f.IssueAsync(key), key));
        }
        var clock = new PumpClock(f.Clock);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock);
        var coordinator = new BatchDeliveryCoordinator(f.Store, clock, gate, channel);
        ConcurrentQueue<SectionKey> refresh = new();
        var pump = new BatchDeliveryPump(f.Store, coordinator, clock, Policy, key =>
        {
            Assert.True(gate.Wait(0)); gate.Release();
            refresh.Enqueue(key); // A real host owns its asynchronous refresh; this callback never awaits it.
        }, new(pageSize: 2, maxWorkPerTurn: 2));
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        await clock.DriveUntilAsync(() => channel.Calls.Count == 2);
        stop.Cancel();
        await run.WaitAsync(Timeout);
        Assert.Equal(keys.Take(6).OrderBy(key => key.Section), refresh.Distinct().OrderBy(key => key.Section));
        var calls = channel.Calls.ToArray();
        Assert.Equal(keys.Skip(6), calls.Select(call => call.Envelope.Key));
        Assert.True(clock.GetElapsedTime(calls[0].Started, calls[1].Started) >= TimeSpan.FromSeconds(1));
        Assert.Equal(6, (await f.Store.ReadPendingDeliveriesAsync(20, default)).Length);
    }

    [Fact]
    public async Task RetryableChannelFailureWaitsFiveMonotonicSecondsWithoutCatchupBurst()
    {
        await using var f = await StoreFixture.CreateAsync();
        await f.AddAsync(StoreFixture.Key()); await f.ObserveAsync(StoreFixture.Key(), 119);
        var clock = new PumpClock(f.Clock);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock) { Handler = (_, _) => Task.FromResult(BatchChannelOutcome.RetryableFailure) };
        var pump = new BatchDeliveryPump(f.Store, new(f.Store, clock, gate, channel), clock, Policy, _ => { });
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        await clock.DriveUntilAsync(() => channel.Calls.Count == 2);
        stop.Cancel(); await run.WaitAsync(Timeout);
        var calls = channel.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.True(clock.GetElapsedTime(calls[0].Started, calls[1].Started) >= TimeSpan.FromSeconds(5));
        Assert.Equal(calls[0].Envelope.NativeDedupeKey, calls[1].Envelope.NativeDedupeKey);
    }

    [Fact]
    public async Task LostCommittedResultIsFlushedByKnownKeyEvenWhenDatabasePageHasNoWork()
    {
        await using var f = await StoreFixture.CreateAsync();
        await f.AddAsync(StoreFixture.Key()); await f.ObserveAsync(StoreFixture.Key(), 119);
        var clock = new PumpClock(f.Clock);
        var port = new ResultFaultPort(f.Store);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock);
        var coordinator = new BatchDeliveryCoordinator(port, clock, gate, channel);
        var flushed = Signal();
        var pump = new BatchDeliveryPump(f.Store, coordinator, clock, Policy, _ => { }, observer: (_, code) =>
        { if (code == "known_result_recorded") flushed.TrySetResult(); });
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        await clock.DriveUntilAsync(() => flushed.Task.IsCompleted);
        stop.Cancel(); await run.WaitAsync(Timeout);
        Assert.Empty(await f.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Single(channel.Calls);
        Assert.Equal(2, port.Commands.Count);
        var commands = port.Commands.ToArray();
        Assert.Same(commands[0], commands[1]);
        Assert.Empty(await coordinator.ReadPendingKnownSectionsAsync());
    }

    [Fact]
    public async Task CancelledRunWaitsForActualChannelAndAuthoritativeResultBeforeStoppingAdmission()
    {
        await using var f = await StoreFixture.CreateAsync();
        await f.AddAsync(StoreFixture.Key()); await f.ObserveAsync(StoreFixture.Key(), 119);
        var clock = new PumpClock(f.Clock);
        var entered = Signal(); var release = new TaskCompletionSource<BatchChannelOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock) { Handler = (_, _) => { entered.SetResult(); return release.Task; } };
        var pump = new BatchDeliveryPump(f.Store, new(f.Store, clock, gate, channel), clock, Policy, _ => { });
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        await entered.Task.WaitAsync(Timeout);
        stop.Cancel();
        Assert.False(run.IsCompleted);
        release.SetResult(BatchChannelOutcome.OsAccepted);
        await run.WaitAsync(Timeout);
        Assert.False(pump.Wake());
        Assert.Empty(await f.Store.ReadPendingDeliveriesAsync(20, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pump.RunAsync(default));
    }

    [Fact]
    public async Task UnresolvedAttemptPausesOnlyDeliveryAndNeverAutoRecoversOrCallsChannel()
    {
        await using var f = await StoreFixture.CreateAsync();
        await f.AddAsync(StoreFixture.Key()); await f.ObserveAsync(StoreFixture.Key(), 119);
        await StoreBatchBehaviorTests.ReserveAsync(f, StoreFixture.Key());
        var clock = new PumpClock(f.Clock);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock);
        ConcurrentQueue<string> notices = new();
        var pump = new BatchDeliveryPump(f.Store, new(f.Store, clock, gate, channel), clock, Policy, _ => { },
            observer: (_, code) => { notices.Enqueue(code); throw new InvalidOperationException("private observer failure"); });
        await pump.RunAsync(default).WaitAsync(Timeout);
        Assert.Contains("recovery_required", notices);
        Assert.Empty(channel.Calls);
        var other = StoreFixture.Key("013");
        await f.AddAsync(other); await f.ObserveAsync(other, 119); // Monitoring/store work remains independent.
        Assert.Equal(2, (await f.Store.ReadPendingDeliveriesAsync(20, default)).Length);
    }

    [Fact]
    public async Task FullRetryTrackerCannotLetAnEarlyStaleKeyReclaimEveryExpiredSlotAndStarveLaterWork()
    {
        await using var f = await StoreFixture.CreateAsync();
        var early = StoreFixture.Key("001"); var later = StoreFixture.Key("002");
        await f.AddAsync(early); await f.ObserveAsync(early, 119);
        await f.CommitAsync(f.Unknown(await f.IssueAsync(early), early));
        await f.AddAsync(later); await f.ObserveAsync(later, 119);
        var clock = new PumpClock(f.Clock);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock);
        var pump = new BatchDeliveryPump(f.Store, new(f.Store, clock, gate, channel), clock, Policy, _ => { },
            new(pageSize: 2, maxWorkPerTurn: 2, maxTrackedSections: 1));
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        try { await clock.DriveUntilAsync(() => channel.Calls.Count == 1); }
        finally { stop.Cancel(); await run.WaitAsync(Timeout); }
        Assert.Equal(later, Assert.Single(channel.Calls).Envelope.Key);
    }

    [Fact]
    public async Task PromotedWorkRemovedDuringScanCannotReserveTrackingCapacityForever()
    {
        await using var f = await StoreFixture.CreateAsync();
        var early = StoreFixture.Key("001"); var removed = StoreFixture.Key("002"); var later = StoreFixture.Key("003");
        await f.AddAsync(early); await f.ObserveAsync(early, 119);
        await f.CommitAsync(f.Unknown(await f.IssueAsync(early), early));
        await f.AddAsync(removed); await f.ObserveAsync(removed, 119);
        var removedEvent = (await f.Store.ReadPendingDeliveriesAsync(20, default)).Single(item => item.Key == removed);
        var clock = new PumpClock(f.Clock);
        var requested = Signal();
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock);
        var pump = new BatchDeliveryPump(f.Store, new(f.Store, clock, gate, channel), clock, Policy, _ => requested.TrySetResult(),
            new(pageSize: 2, maxWorkPerTurn: 2, maxTrackedSections: 1));
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        try
        {
            await requested.Task.WaitAsync(Timeout);
            await f.Store.RecordDeliveryResultAsync(new(removedEvent.EventId, removedEvent.DeliveryRevision,
                DeliveryStatus.OsAccepted, 1, f.Clock.GetUtcNow()), default);
            await f.AddAsync(later); await f.ObserveAsync(later, 119);
            await clock.DriveUntilAsync(() => channel.Calls.Count == 1);
        }
        finally { stop.Cancel(); await run.WaitAsync(Timeout); }
        Assert.Equal(later, Assert.Single(channel.Calls).Envelope.Key);
    }

    [Fact]
    public async Task FullWakeChannelRetainsSourceChangeAndFreshCallbackFailureCannotKillActor()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key); await f.ObserveAsync(key, 119);
        await f.CommitAsync(f.Unknown(await f.IssueAsync(key), key));
        var clock = new PumpClock(f.Clock);
        var initialStamp = clock.GetTimestamp();
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock);
        var twice = Signal();
        var calls = 0; var admitted = false; var coalesced = true;
        ConcurrentQueue<string> notices = new();
        BatchDeliveryPump pump = null!;
        pump = new(f.Store, new(f.Store, clock, gate, channel), clock, Policy, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                admitted = pump.Wake();
                coalesced = pump.Wake(sourceChanged: true); // The actor cannot drain while this callback runs.
            }
            else twice.TrySetResult();
            throw new InvalidOperationException("private source refresh detail");
        }, observer: (_, code) => notices.Enqueue(code));
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        try { await twice.Task.WaitAsync(Timeout); }
        finally { stop.Cancel(); await run.WaitAsync(Timeout); }
        Assert.True(admitted);
        Assert.False(coalesced);
        Assert.Equal(2, calls);
        Assert.Equal(0, clock.GetElapsedTime(initialStamp, clock.GetTimestamp()).Ticks);
        Assert.Contains("fresh_request_failed", notices);
        Assert.Empty(channel.Calls);
    }

    [Fact]
    public async Task ShutdownRetriesKnownCanonicalResultWithinBoundAndReportsRemainingUncertaintyWithoutAck()
    {
        await using var f = await StoreFixture.CreateAsync();
        await f.AddAsync(StoreFixture.Key()); await f.ObserveAsync(StoreFixture.Key(), 119);
        var clock = new PumpClock(f.Clock);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock);
        var port = new ResultFaultPort(f.Store, failBeforeCommit: true);
        var coordinator = new BatchDeliveryCoordinator(port, clock, gate, channel);
        var failed = Signal();
        ConcurrentQueue<string> notices = new();
        var pump = new BatchDeliveryPump(f.Store, coordinator, clock, Policy, _ => { }, new(shutdownFlushAttempts: 3),
            observer: (_, code) => { notices.Enqueue(code); if (code == "known_result_not_recorded") failed.TrySetResult(); });
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        await failed.Task.WaitAsync(Timeout);
        stop.Cancel(); await run.WaitAsync(Timeout);
        Assert.Single(channel.Calls);
        Assert.Equal(4, port.Commands.Count); // One live write plus three shutdown retries.
        var commands = port.Commands.ToArray();
        Assert.All(commands, command => Assert.Same(commands[0], command));
        Assert.Contains("shutdown_results_pending", notices);
        Assert.Single(await coordinator.ReadPendingKnownSectionsAsync());
        var pending = Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(DeliveryStatus.Pending, pending.Status);
        Assert.Equal(0, pending.Attempts);
    }

    [Fact]
    public async Task IncompleteFoldYieldsToLaterSectionAndNeverSubmitsPartialCoverage()
    {
        await using var f = await StoreFixture.CreateAsync();
        var early = StoreFixture.Key("001"); var later = StoreFixture.Key("002");
        await f.AddAsync(early, false); await f.ObserveAsync(early, 120);
        await f.ObserveAsync(early, 119); await f.ObserveAsync(early, 118); await f.ObserveAsync(early, 117);
        await f.AddAsync(later); await f.ObserveAsync(later, 119);
        var clock = new PumpClock(f.Clock);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new PumpChannel(clock);
        var pump = new BatchDeliveryPump(f.Store, new(f.Store, clock, gate, channel), clock, Policy, _ => { },
            new(pageSize: 1, maxWorkPerTurn: 1, maxFoldPages: 1));
        using var stop = new CancellationTokenSource();
        var run = pump.RunAsync(stop.Token);
        try { await clock.DriveUntilAsync(() => channel.Calls.Count == 2); }
        finally { stop.Cancel(); await run.WaitAsync(Timeout); }
        var calls = channel.Calls.ToArray();
        Assert.Equal(later, calls[0].Envelope.Key);
        Assert.Equal(early, calls[1].Envelope.Key);
        Assert.Equal(3, calls[1].Envelope.Coverage.EventCount);
    }
}
