using SnuWatcher.Windows.Hosting;
using Xunit;

namespace SnuWatcher.Windows.Tests.Hosting;

public sealed class RetryableTeardownTests
{
    [Fact]
    public async Task FailedUnregisterShowsBlockedAndCanBeRetriedWithoutRepeatingSuccess()
    {
        var registered = true;
        var attempts = 0;
        var failure = new InvalidOperationException("Injected unregister failure.");
        var teardown = new RetryableTeardown(() =>
        {
            if (registered && ++attempts == 1) throw failure;
            registered = false;
            return Task.CompletedTask;
        });

        Assert.False(await teardown.TryRunAsync());
        Assert.True(registered);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => teardown.RunAsync()));
        Assert.False(await teardown.TryRunAsync());
        Assert.Equal(1, attempts);

        Assert.True(await teardown.TryRunAsync(retryFailed: true));
        Assert.False(registered);
        Assert.True(await teardown.TryRunAsync(retryFailed: true));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EveryNonOomFinalizerFailureBecomesBlocked(int failureStage)
    {
        var reached = new List<int>();
        var teardown = new RetryableTeardown(async () =>
        {
            for (var stage = 0; stage < 3; stage++)
            {
                reached.Add(stage);
                await Task.CompletedTask;
                if (stage == failureStage) throw new IOException("Injected finalizer failure.");
            }
        });

        Assert.False(await teardown.TryRunAsync());
        Assert.Equal(Enumerable.Range(0, failureStage + 1), reached);
    }

    [Fact]
    public async Task ConcurrentQuitAndFailureOwnersShareActualWorkEvenWhenRetryIsRequested()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var teardown = new RetryableTeardown(async () =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
        });

        var first = teardown.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = teardown.RunAsync(retryFailed: true);
        var blockedResult = teardown.TryRunAsync(retryFailed: true);
        try
        {
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            Assert.False(blockedResult.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second); }
        Assert.True(await blockedResult);
        Assert.Same(first, teardown.RunAsync(retryFailed: true));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReentrantFinalizerSeesItsAlreadyOwnedAttempt()
    {
        RetryableTeardown? teardown = null;
        Task? reentrant = null;
        var calls = 0;
        teardown = new RetryableTeardown(() =>
        {
            calls++;
            reentrant = teardown!.RunAsync(retryFailed: true);
            return Task.CompletedTask;
        });

        var actual = teardown.RunAsync();
        await actual;

        Assert.Same(actual, reentrant);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CanceledFinalizationIsBlockedUntilExplicitRetry()
    {
        var calls = 0;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var teardown = new RetryableTeardown(() => ++calls == 1
            ? Task.FromCanceled(canceled.Token) : Task.CompletedTask);

        Assert.False(await teardown.TryRunAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => teardown.RunAsync());
        Assert.True(await teardown.TryRunAsync(retryFailed: true));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OutOfMemoryIsPropagatedRatherThanPresentedAsARecoverableBlock()
    {
        var failure = new OutOfMemoryException("Injected fatal failure without allocating memory.");
        var teardown = new RetryableTeardown(() => throw failure);

        Assert.Same(failure, await Assert.ThrowsAsync<OutOfMemoryException>(() => teardown.TryRunAsync()));
    }

    [Fact]
    public void MissingFinalizerIsRejected() => Assert.Throws<ArgumentNullException>(() => new RetryableTeardown(null!));
}
