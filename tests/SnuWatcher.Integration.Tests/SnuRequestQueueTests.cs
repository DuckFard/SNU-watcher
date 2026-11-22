using SnuWatcher.Application.Sources;
using SnuWatcher.Infrastructure.Snu;
using SnuWatcher.Infrastructure.Snu.RequestQueue;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuRequestQueueTests
{
    [Fact]
    public async Task TimestampZeroIsARealStartAndTheNextStartWaitsTwoSeconds()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, new(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, await queue.DispatchAsync(SourceRequestPurpose.Interactive, _ => Task.FromResult(1)));
        var next = queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(clock.GetTimestamp()));
        Assert.Equal(TimeSpan.FromSeconds(2), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(next.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task NoSecondCallbackStartsUntilTheFirstActuallyCompletes()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, new(TimeSpan.FromSeconds(2)));
        var entered = Barrier();
        var release = Barrier();
        var first = queue.DispatchAsync(SourceRequestPurpose.Interactive, async token => { entered.SetResult(); await release.Task.WaitAsync(token); return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var nextStarted = Barrier();
        var next = queue.DispatchAsync(SourceRequestPurpose.Watch, _ => { nextStarted.SetResult(); return Task.FromResult(2); });
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(nextStarted.Task.IsCompleted);
        release.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FairnessAllowsAtMostTwoInteractiveStartsWhileWatchWaitsAndPreservesLaneFifo()
    {
        await using var queue = Immediate();
        var entered = Barrier();
        var release = Barrier();
        var first = queue.DispatchAsync(SourceRequestPurpose.Watch, async token => { entered.SetResult(); await release.Task.WaitAsync(token); return 0; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var order = new List<string>();
        Task<int> Enqueue(string label, SourceRequestPurpose purpose) => queue.DispatchAsync(purpose, _ => { order.Add(label); return Task.FromResult(1); });
        var pending = new[] { Enqueue("I1", SourceRequestPurpose.Interactive), Enqueue("I2", SourceRequestPurpose.Interactive),
            Enqueue("I3", SourceRequestPurpose.Interactive), Enqueue("W1", SourceRequestPurpose.Watch), Enqueue("W2", SourceRequestPurpose.Watch) };
        release.SetResult();
        await first;
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "I1", "I2", "W1", "I3", "W2" }, order);
    }

    [Fact]
    public async Task CanceledQueuedWorkImmediatelyFreesTheBoundedSlotWithoutDispatch()
    {
        await using var queue = Immediate(capacity: 1);
        var entered = Barrier();
        var release = Barrier();
        var first = queue.DispatchAsync(SourceRequestPurpose.Interactive, async token => { entered.SetResult(); await release.Task.WaitAsync(token); return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var canceled = new CancellationTokenSource();
        var canceledStarted = false;
        var pending = queue.DispatchAsync(SourceRequestPurpose.Watch, _ => { canceledStarted = true; return Task.FromResult(2); }, canceled.Token);
        var full = await Assert.ThrowsAsync<SnuSourceException>(() => queue.DispatchAsync(SourceRequestPurpose.Interactive, _ => Task.FromResult(3)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SourceFailureKind.LimitReached, full.Kind);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var replacement = queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(4));
        release.SetResult();
        await first;
        Assert.Equal(4, await replacement.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(canceledStarted);
    }

    [Fact]
    public async Task PreCanceledWorkConsumesNoCapacityAndNeverInvokesTheCallback()
    {
        await using var queue = Immediate();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.DispatchAsync<int>(SourceRequestPurpose.Watch,
            _ => throw new InvalidOperationException("Must not run"), canceled.Token));
        Assert.Equal(0, queue.GetSnapshot().QueuedWatch);
        Assert.Equal(1, await queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(1)));
    }

    [Fact]
    public async Task UnexpectedAndNonGlobalSourceFailuresDoNotStopTheWorker()
    {
        await using var queue = Immediate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DispatchAsync<int>(SourceRequestPurpose.Interactive,
            _ => throw new InvalidOperationException("fixture")));
        await Assert.ThrowsAsync<SnuSourceException>(() => queue.DispatchAsync<int>(SourceRequestPurpose.Watch,
            _ => throw new SnuSourceException(SourceFailureKind.ParseChanged, "fixture")));
        Assert.Equal(7, await queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(7)));
    }

    [Fact]
    public async Task CancellationDoesNotReleaseAnUncooperativeCallbackOrMaskItsCompletedResult()
    {
        await using var queue = Immediate();
        using var cancellation = new CancellationTokenSource();
        var entered = Barrier();
        var release = Barrier();
        CancellationToken activeToken = default;
        var first = queue.DispatchAsync(SourceRequestPurpose.Watch, async token =>
        { activeToken = token; entered.SetResult(); await release.Task; return 41; }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = queue.DispatchAsync(SourceRequestPurpose.Interactive, _ => Task.FromResult(42));
        try
        {
            cancellation.Cancel();
            Assert.True(activeToken.IsCancellationRequested);
            Assert.False(first.IsCompleted);
            Assert.False(next.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(41, await first);
        Assert.Equal(42, await next);
    }

    [Theory]
    [InlineData(SourceFailureKind.RateLimited, 60)]
    [InlineData(SourceFailureKind.Maintenance, 120)]
    public async Task GlobalFallbackPolicyIsAppliedBeforeTheNextWorkStarts(SourceFailureKind kind, int seconds)
    {
        var clock = new QueueClock();
        await using var queue = Immediate(clock);
        await Assert.ThrowsAsync<SnuSourceException>(() => queue.DispatchAsync<int>(SourceRequestPurpose.Watch,
            _ => throw new SnuSourceException(kind, "fixture")));
        Assert.Equal(TimeSpan.FromSeconds(seconds), queue.GetSnapshot().CooldownRemaining);
        var next = queue.DispatchAsync(SourceRequestPurpose.Interactive, _ => Task.FromResult(1));
        Assert.Equal(TimeSpan.FromSeconds(seconds), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.False(next.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DeclaredMaintenanceExtendsAnExistingCooldownAndUtcRollbackCannotShortenIt()
    {
        var clock = new QueueClock();
        await using var queue = Immediate(clock);
        queue.DeclareMaintenance(TimeSpan.FromSeconds(200));
        clock.Advance(TimeSpan.FromSeconds(10));
        clock.AdjustUtc(TimeSpan.FromDays(-7));
        queue.DeclareMaintenance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(190), queue.GetSnapshot().CooldownRemaining);
        var next = queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(1));
        Assert.Equal(TimeSpan.FromSeconds(190), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(190));
        Assert.Equal(1, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AccessPauseRejectsBothPurposesAndResumeRetainsTheCooldown()
    {
        var clock = new QueueClock();
        await using var queue = Immediate(clock);
        var entered = Barrier();
        var release = Barrier();
        var first = queue.DispatchAsync<int>(SourceRequestPurpose.Interactive, async token =>
        { entered.SetResult(); await release.Task.WaitAsync(token); throw new SnuSourceException(SourceFailureKind.AccessRestricted, "fixture"); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedWatch = queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(1));
        var queuedInteractive = queue.DispatchAsync(SourceRequestPurpose.Interactive, _ => Task.FromResult(1));
        queue.DeclareMaintenance(TimeSpan.FromSeconds(180));
        release.SetResult();
        await Assert.ThrowsAsync<SnuSourceException>(() => first);
        foreach (var task in new[] { queuedWatch, queuedInteractive,
            queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(1)),
            queue.DispatchAsync(SourceRequestPurpose.Interactive, _ => Task.FromResult(1)) })
            Assert.Equal(SourceFailureKind.AccessRestricted, (await Assert.ThrowsAsync<SnuSourceException>(() => task)).Kind);
        queue.ResumeAccess();
        Assert.False(queue.GetSnapshot().AccessPaused);
        Assert.Equal(TimeSpan.FromSeconds(180), queue.GetSnapshot().CooldownRemaining);
        var resumed = queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(2));
        Assert.Equal(TimeSpan.FromSeconds(180), await clock.NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(180));
        Assert.Equal(2, await resumed.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(90000, 90000)]
    public async Task ExplicitUnavailableRetryAfterIsNormalizedWithoutCapping(int inputSeconds, int expectedSeconds)
    {
        await using var queue = Immediate();
        await Assert.ThrowsAsync<SnuSourceException>(() => queue.DispatchAsync<int>(SourceRequestPurpose.Watch,
            _ => throw new SnuSourceException(SourceFailureKind.Unavailable, "fixture", TimeSpan.FromSeconds(inputSeconds)) { HttpStatusCode = 503 }));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), queue.GetSnapshot().CooldownRemaining);
    }

    [Fact]
    public async Task HugeServerDelayIsKeptButTimerWaitsUseSafeChunks()
    {
        var clock = new QueueClock();
        await using var queue = Immediate(clock);
        queue.DeclareMaintenance(TimeSpan.MaxValue);
        var pending = queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(1));
        var wait = await clock.NextTimerAsync();
        Assert.InRange(wait, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1));
        Assert.Equal(TimeSpan.MaxValue, queue.GetSnapshot().CooldownRemaining);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task DisposalCancelsPendingAndInflightButWaitsForTheActualCallback()
    {
        var queue = Immediate();
        var entered = Barrier();
        var release = Barrier();
        CancellationToken activeToken = default;
        var first = queue.DispatchAsync(SourceRequestPurpose.Watch, async token =>
        { activeToken = token; entered.SetResult(); await release.Task; return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = queue.DispatchAsync(SourceRequestPurpose.Interactive, _ => Task.FromResult(2));
        var disposal = queue.DisposeAsync().AsTask();
        try
        {
            Assert.True(activeToken.IsCancellationRequested);
            Assert.False(disposal.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.DispatchAsync(SourceRequestPurpose.Watch, _ => Task.FromResult(3)));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await first);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task DisposingFromTheCallbackFailsExplicitlyInsteadOfWaitingForItself()
    {
        await using var queue = Immediate();
        await queue.DispatchAsync(SourceRequestPurpose.Watch, _ =>
        {
            Assert.Throws<InvalidOperationException>(() => queue.Dispose());
            return Task.FromResult(1);
        });
    }

    [Theory]
    [InlineData(0, 128)]
    [InlineData(1, 128)]
    [InlineData(61, 128)]
    [InlineData(2, 0)]
    [InlineData(2, 1025)]
    public void ProductionQueueOptionsRejectInvalidSpacingAndBounds(int seconds, int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceQueueOptions(TimeSpan.FromSeconds(seconds), capacity));

    private static SnuRequestDispatcher Immediate(QueueClock? clock = null, int capacity = 128) =>
        new(clock ?? new QueueClock(), SourceQueueOptions.ForTesting(TimeSpan.Zero, capacity));
    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
