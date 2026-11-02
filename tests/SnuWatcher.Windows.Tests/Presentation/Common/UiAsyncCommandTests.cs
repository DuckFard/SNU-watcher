using System.Collections.Concurrent;
using System.Windows.Input;
using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Common;

public sealed class UiAsyncCommandTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task DuplicateExecutionDoesNotStartAnotherDriverAndEventsUseDispatcher()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var entered = Barrier();
        var release = Barrier();
        var calls = 0;
        var eventThreads = new ConcurrentQueue<int>();
        await using var command = new UiAsyncCommand(async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
        }, dispatcher, _ => Assert.Fail("No error was expected."));
        command.PropertyChanged += (_, _) => eventThreads.Enqueue(Environment.CurrentManagedThreadId);
        command.CanExecuteChanged += (_, _) => eventThreads.Enqueue(Environment.CurrentManagedThreadId);
        var run = command.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.True(command.IsExecuting);
            Assert.False(command.CanExecute(null));
            Assert.Equal(UiCommandOutcome.Busy, await command.ExecuteAsync());
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(UiCommandOutcome.Completed, await run.WaitAsync(Deadline));
        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
        Assert.NotEmpty(eventThreads);
        Assert.All(eventThreads, thread => Assert.Equal(dispatcher.ThreadId, thread));
    }

    [Fact]
    public async Task CancellationDoesNotInventCompletionWhenDriverIgnoresIt()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var entered = Barrier();
        var release = Barrier();
        await using var command = new UiAsyncCommand(async _ => { entered.SetResult(); await release.Task; }, dispatcher, _ => { });
        var run = command.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            command.Cancel();
            Assert.True(command.IsCancellationRequested);
            Assert.True(command.IsExecuting);
            Assert.False(run.IsCompleted);
            Assert.Equal(UiCommandOutcome.Busy, await command.ExecuteAsync());
        }
        finally { release.TrySetResult(); }
        Assert.Equal(UiCommandOutcome.Completed, await run.WaitAsync(Deadline));
    }

    [Fact]
    public async Task BlockingCancellationCallbackDoesNotBlockCancelAndDisposalOwnsIt()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var entered = Barrier();
        var callbackEntered = Barrier();
        using var callbackRelease = new ManualResetEventSlim();
        var driverRelease = Barrier();
        await using var command = new UiAsyncCommand(async token =>
        {
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!callbackRelease.Wait(Deadline)) throw new TimeoutException("The private callback barrier was not released.");
            });
            entered.SetResult();
            await driverRelease.Task;
        }, dispatcher, _ => { });
        var run = command.ExecuteAsync();
        Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await dispatcher.InvokeAsync(command.Cancel).WaitAsync(Deadline);
            await callbackEntered.Task.WaitAsync(Deadline);
            disposing = command.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.True(command.IsExecuting);
            Assert.False(command.CanExecute(null));
        }
        finally { driverRelease.TrySetResult(); callbackRelease.Set(); }
        Assert.Equal(UiCommandOutcome.Completed, await run.WaitAsync(Deadline));
        await disposing!.WaitAsync(Deadline);
        Assert.Equal(UiCommandOutcome.Unavailable, await command.ExecuteAsync());
    }

    [Fact]
    public async Task ThrowingCancellationCallbackIsObservedAsFixedErrorAndDoesNotEscapeCancel()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var entered = Barrier();
        var callbackEntered = Barrier();
        var release = Barrier();
        var errors = new ConcurrentQueue<string>();
        await using var command = new UiAsyncCommand(async token =>
        {
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                throw new IOException("private cancellation detail");
            });
            entered.SetResult();
            await release.Task;
        }, dispatcher, errors.Enqueue);
        var run = command.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            command.Cancel();
            await callbackEntered.Task.WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(UiCommandOutcome.Failed, await run.WaitAsync(Deadline));
        Assert.Equal("ui_command_failed", Assert.Single(errors));
        Assert.False(command.IsExecuting);
    }

    [Fact]
    public async Task DriverCancellationAndUnexpectedDriverFailureHaveDifferentOutcomes()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var entered = Barrier();
        var errors = new ConcurrentQueue<string>();
        await using var canceled = new UiAsyncCommand(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, dispatcher, errors.Enqueue);
        var run = canceled.ExecuteAsync();
        await entered.Task.WaitAsync(Deadline);
        canceled.Cancel();
        Assert.Equal(UiCommandOutcome.Canceled, await run.WaitAsync(Deadline));
        Assert.Empty(errors);

        await using var failed = new UiAsyncCommand(_ => throw new IOException("private database path"), dispatcher, errors.Enqueue);
        Assert.Equal(UiCommandOutcome.Failed, await failed.ExecuteAsync().WaitAsync(Deadline));
        Assert.Equal("ui_command_failed", Assert.Single(errors));
        Assert.DoesNotContain(errors, error => error.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ICommandBoundaryObservesFailureEvenWhenNoCallerAwaitsIt()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var command = new UiAsyncCommand(_ => throw new InvalidOperationException("private"), dispatcher, code => reported.TrySetResult(code));
        ((ICommand)command).Execute(null);
        Assert.Equal("ui_command_failed", await reported.Task.WaitAsync(Deadline));
        await command.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.False(command.IsExecuting);
    }

    [Fact]
    public async Task FailedUiPublicationAfterDriverStopsClosesAdmissionAndStillDrains()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var entered = Barrier();
        var release = Barrier();
        var errors = new ConcurrentQueue<string>();
        var driverEnded = false;
        await using var command = new UiAsyncCommand(async _ =>
        {
            entered.SetResult();
            await release.Task;
            driverEnded = true;
        }, dispatcher, errors.Enqueue);
        var run = command.ExecuteAsync();
        try { await entered.Task.WaitAsync(Deadline); dispatcher.FailNextInvoke(); }
        finally { release.TrySetResult(); }
        Assert.Equal(UiCommandOutcome.Failed, await run.WaitAsync(Deadline));
        Assert.True(driverEnded);
        Assert.False(command.CanExecute(null));
        Assert.Equal(UiCommandOutcome.Unavailable, await command.ExecuteAsync());
        Assert.Equal("ui_command_failed", Assert.Single(errors));
        await command.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task ErrorCallbackFailureIsIsolatedFromExecutionAndDisposal()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        await using var command = new UiAsyncCommand(_ => throw new IOException("private"), dispatcher,
            _ => throw new InvalidOperationException("private reporter"));
        Assert.Equal(UiCommandOutcome.Failed, await command.ExecuteAsync().WaitAsync(Deadline));
        await command.DisposeAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task PreCanceledAndUnavailableCommandsNeverInvokeDriver()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var calls = 0;
        await using var command = new UiAsyncCommand(_ => { calls++; return Task.CompletedTask; }, dispatcher, _ => { }, () => false);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Equal(UiCommandOutcome.Canceled, await command.ExecuteAsync(canceled.Token));
        Assert.Equal(UiCommandOutcome.Unavailable, await command.ExecuteAsync());
        Assert.False(command.CanExecute(null));
        command.Cancel();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReentrantExecutionRetainsPreviousFinalPublicationBeforeNewDriver()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var calls = 0;
        Task<UiCommandOutcome>? second = null;
        await using var command = new UiAsyncCommand(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, dispatcher, _ => { });
        command.CanExecuteChanged += (_, _) =>
        {
            if (command.CanExecute(null) && calls == 1 && second is null)
                second = command.ExecuteAsync();
        };
        Assert.Equal(UiCommandOutcome.Completed, await command.ExecuteAsync().WaitAsync(Deadline));
        Assert.NotNull(second);
        Assert.Equal(UiCommandOutcome.Completed, await second!.WaitAsync(Deadline));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PredicateAndPostFailuresAreSanitizedAndCloseAdmission()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var errors = new ConcurrentQueue<string>();
        await using var predicate = new UiAsyncCommand(_ => Task.CompletedTask, dispatcher, errors.Enqueue,
            () => throw new IOException("private predicate"));
        Assert.False(predicate.CanExecute(null));
        await dispatcher.InvokeAsync(() => { });
        Assert.Equal("ui_command_failed", Assert.Single(errors));
        Assert.Equal(UiCommandOutcome.Unavailable, await predicate.ExecuteAsync());

        await using var post = new UiAsyncCommand(_ => Task.CompletedTask, dispatcher, _ => { });
        dispatcher.FailPost = true;
        post.NotifyCanExecuteChanged();
        Assert.False(post.CanExecute(null));
        Assert.Equal(UiCommandOutcome.Unavailable, await post.ExecuteAsync());
        dispatcher.FailPost = false;
    }

    [Fact]
    public async Task NullDependenciesAreRejectedBeforeAnyWork()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        Assert.Throws<ArgumentNullException>(() => new UiAsyncCommand(null!, dispatcher, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new UiAsyncCommand(_ => Task.CompletedTask, null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new UiAsyncCommand(_ => Task.CompletedTask, dispatcher, null!));
    }
}
