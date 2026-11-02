using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Core;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class OwnedRefreshBridgeTests
{
    [Fact]
    public async Task UnexpectedWorkerFailureIsObservableAndQueuedWaitersReceiveBoundedFailure()
    {
        var bridge = new OwnedRefreshBridge((_, _) => throw new IOException("private refresh detail"), maxDistinctKeys: 2);
        var running = bridge.RunAsync(default);

        var result = await bridge.RequestAsync(Key()).WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => running);

        Assert.Equal(OwnedRefreshDisposition.Failed, result.Disposition);
        Assert.Equal("refresh_dependency_failed", result.ReasonCode);
        Assert.DoesNotContain("private", failure.Message);
        Assert.False(bridge.TryRequest(Key("013")));
    }

    [Fact]
    public async Task ClosingAdmissionBeforeRunRejectsRefreshWithoutCallingMonitor()
    {
        var calls = 0;
        var bridge = new OwnedRefreshBridge((_, _) => { calls++; return Task.FromResult<WatchRefreshResult>(null!); });
        bridge.CloseAdmissionAndCancelWaiters();

        var result = await bridge.RequestAsync(Key());

        Assert.Equal(OwnedRefreshDisposition.Stopped, result.Disposition);
        Assert.False(bridge.TryRequest(Key()));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DuplicateRunningKeysCoalesceAndCancelingOneWaiterDoesNotCancelOwnedRefresh()
    {
        var monitorResult = await MonitorResultAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<WatchRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken ownedToken = default;
        var bridge = new OwnedRefreshBridge((_, token) =>
        {
            ownedToken = token;
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            return finish.Task;
        });
        using var stop = new CancellationTokenSource();
        using var waiter = new CancellationTokenSource();
        var running = bridge.RunAsync(stop.Token);
        var first = bridge.RequestAsync(Key(), waiter.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = bridge.RequestAsync(Key());
        Assert.True(bridge.TryRequest(Key()));
        waiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(ownedToken.IsCancellationRequested);
        Assert.False(second.IsCompleted);
        finish.SetResult(monitorResult);

        var result = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
        Assert.Equal(OwnedRefreshDisposition.Completed, result.Disposition);
        Assert.Same(monitorResult, result.MonitorResult);
        Assert.Equal(Key(), result.Key);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExactlyFourOwnedWorkersRunAndDistinctQueuedKeysRemainBounded()
    {
        var monitorResult = await MonitorResultAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<WatchRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var calls = 0;
        var bridge = new OwnedRefreshBridge(async (_, _) =>
        {
            Interlocked.Increment(ref active);
            if (Interlocked.Increment(ref calls) == 4) started.TrySetResult();
            try { return await finish.Task; }
            finally { Interlocked.Decrement(ref active); }
        }, maxDistinctKeys: 5);
        using var stop = new CancellationTokenSource();
        var running = bridge.RunAsync(stop.Token);
        var requests = Enumerable.Range(1, 5).Select(i => bridge.RequestAsync(Key(i.ToString("000")))).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, Volatile.Read(ref active));
        Assert.Equal(5, bridge.PendingCount);
        Assert.False(bridge.TryRequest(Key("006")));
        Assert.Equal(1, bridge.RejectedCount);
        Assert.Equal("refresh_capacity", bridge.LastResult!.ReasonCode);
        finish.SetResult(monitorResult);

        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal(OwnedRefreshDisposition.Completed, result.Disposition));
        Assert.Equal(5, calls);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CloseDetachesWaitersPromptlyButRunStillOwnsIgnoringDelegateUntilActualCompletion()
    {
        var monitorResult = await MonitorResultAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<WatchRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new OwnedRefreshBridge((_, _) => { started.TrySetResult(); return finish.Task; });
        var running = bridge.RunAsync(default);
        var waiter = bridge.RequestAsync(Key());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        bridge.CloseAdmissionAndCancelWaiters();

        Assert.Equal(OwnedRefreshDisposition.Stopped, (await waiter.WaitAsync(TimeSpan.FromSeconds(5))).Disposition);
        Assert.False(running.IsCompleted);
        finish.SetResult(monitorResult);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, bridge.PendingCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.RunAsync(default));
    }

    [Fact]
    public async Task NormalOwnedWaiterCancellationDoesNotBecomeAnActorFailure()
    {
        var result = await MonitorResultAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new OwnedRefreshBridge(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return result;
        });
        using var stop = new CancellationTokenSource();
        var running = bridge.RunAsync(stop.Token);
        var waiter = bridge.RequestAsync(Key());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();

        Assert.Equal(OwnedRefreshDisposition.Stopped, (await waiter.WaitAsync(TimeSpan.FromSeconds(5))).Disposition);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, bridge.PendingCount);
    }

    [Fact]
    public async Task ClosingQueuedWorkNeverStartsTheFifthJoinAfterTheFourRunningJoinsFinish()
    {
        var result = await MonitorResultAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<WatchRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var bridge = new OwnedRefreshBridge((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 4) started.TrySetResult();
            return finish.Task;
        }, maxDistinctKeys: 5);
        var running = bridge.RunAsync(default);
        var waiters = Enumerable.Range(1, 5).Select(i => bridge.RequestAsync(Key(i.ToString("000")))).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bridge.CloseAdmissionAndCancelWaiters();
        Assert.All(await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5)), value =>
            Assert.Equal(OwnedRefreshDisposition.Stopped, value.Disposition));
        finish.SetResult(result);

        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task CancellationCallbackCompletionAndFailureRemainOwned()
    {
        var result = await MonitorResultAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCancellation = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        var bridge = new OwnedRefreshBridge(async (_, token) =>
        {
            registration = token.Register(() =>
            {
                cancellationStarted.TrySetResult();
                if (!releaseCancellation.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture cancellation was not released.");
                throw new IOException("private cancellation callback details");
            });
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return result;
        });
        var running = bridge.RunAsync(default);
        var waiter = bridge.RequestAsync(Key());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = Task.Run(bridge.CloseAdmissionAndCancelWaiters);
        await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(running.IsCompleted); }
        finally { releaseCancellation.Set(); }
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        registration.Dispose();
        Assert.Equal("refresh_cancellation_failed", failure.Message);
        Assert.Equal(OwnedRefreshDisposition.Stopped, (await waiter).Disposition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public void DistinctKeyBoundIsValidated(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OwnedRefreshBridge((_, _) => Task.FromResult<WatchRefreshResult>(null!), capacity));
    }

    private static async Task<WatchRefreshResult> MonitorResultAsync()
    {
        var store = new FakeMonitorStore();
        var clock = new SplitMonitorClock();
        var loop = new WatchMonitorLoop(store, new FakeMonitorSource(store, clock), new FakeMonitorPolicy(), clock,
            new(WatchMonitorProfile.Faster, jitterPercent: 0));
        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);
        try { return await loop.RefreshAsync(Key(), default).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static SectionKey Key(string section = "012") => new(2026, "2", "1", "F32.102", section, "000");
}
