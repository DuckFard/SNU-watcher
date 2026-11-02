using System.Collections.Concurrent;
using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class NotificationCleanupPolicyTests
{
    [Fact]
    public async Task RetainedNotificationsMustFinishClearingBeforeUnregisterAndSuccess()
    {
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var cleanup = NotificationCleanupPolicy.ClearAndUnregisterAsync(true,
            () => { calls.Enqueue("clear"); return cleared.Task; },
            () => calls.Enqueue("unregister"), TimeSpan.FromSeconds(10));

        Assert.False(cleanup.IsCompleted);
        Assert.Equal(new[] { "clear" }, calls);
        cleared.SetResult();
        await cleanup;
        Assert.Equal(new[] { "clear", "unregister" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearFailurePreventsUnregistration(bool synchronousFailure)
    {
        var error = new InvalidOperationException("Clear failed.");
        var unregistered = false;

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NotificationCleanupPolicy.ClearAndUnregisterAsync(true,
                () => synchronousFailure ? throw error : Task.FromException(error),
                () => unregistered = true, TimeSpan.FromSeconds(10)));

        Assert.Same(error, observed);
        Assert.False(unregistered);
    }

    [Fact]
    public async Task UnregisterFailureCannotBeReportedAsSuccessfulCleanup()
    {
        var error = new InvalidOperationException("Unregister failed.");
        var clearFinished = false;
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NotificationCleanupPolicy.ClearAndUnregisterAsync(true,
                () => { clearFinished = true; return Task.CompletedTask; },
                () => throw error, TimeSpan.FromSeconds(10)));

        Assert.True(clearFinished);
        Assert.Same(error, observed);
    }

    [Fact]
    public async Task CanceledRemovalCannotUnregisterOrReportSuccess()
    {
        var unregistered = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NotificationCleanupPolicy.ClearAndUnregisterAsync(true,
                () => Task.FromCanceled(new CancellationToken(true)),
                () => unregistered = true, TimeSpan.FromSeconds(10)));

        Assert.False(unregistered);
    }

    [Fact]
    public async Task DeadlineFailureDoesNotUnregisterEvenIfClearingCompletesLater()
    {
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unregistered = false;
        var cleanup = NotificationCleanupPolicy.ClearAndUnregisterAsync(true,
            () => cleared.Task, () => unregistered = true, TimeSpan.Zero);

        Assert.True(cleanup.IsCompleted);
        await Assert.ThrowsAsync<TimeoutException>(() => cleanup);
        cleared.SetResult();
        await cleared.Task;
        Assert.False(unregistered);
    }

    [Fact]
    public async Task FailedRegistrationCannotTurnCleanupIntoASilentSuccess()
    {
        var nativeCallAttempted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NotificationCleanupPolicy.ClearAndUnregisterAsync(false,
                () => { nativeCallAttempted = true; return Task.CompletedTask; },
                () => nativeCallAttempted = true, TimeSpan.FromSeconds(10)));

        Assert.False(nativeCallAttempted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOperationsAreRejectedBeforeAnyNativeOperation(bool missingClear)
    {
        var nativeCallAttempted = false;
        var error = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            NotificationCleanupPolicy.ClearAndUnregisterAsync(true,
                missingClear ? null! : () => { nativeCallAttempted = true; return Task.CompletedTask; },
                missingClear ? () => nativeCallAttempted = true : null!, TimeSpan.FromSeconds(10)));

        Assert.Equal(missingClear ? "clearNotifications" : "unregister", error.ParamName);
        Assert.False(nativeCallAttempted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task UnboundedOrInvalidWaitsAreRejectedBeforeNativeCalls(int milliseconds)
    {
        var nativeCallAttempted = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NotificationCleanupPolicy.ClearAndUnregisterAsync(true,
                () => { nativeCallAttempted = true; return Task.CompletedTask; },
                () => nativeCallAttempted = true, TimeSpan.FromMilliseconds(milliseconds)));

        Assert.False(nativeCallAttempted);
    }

    [Fact]
    public async Task AlreadyCompletedClearCanSucceedWithoutWaiting()
    {
        var unregistered = false;
        await NotificationCleanupPolicy.ClearAndUnregisterAsync(true,
            () => Task.CompletedTask, () => unregistered = true, TimeSpan.Zero);

        Assert.True(unregistered);
        Assert.Equal(TimeSpan.FromSeconds(10), NotificationCleanupPolicy.MaximumRemovalWait);
    }
}
