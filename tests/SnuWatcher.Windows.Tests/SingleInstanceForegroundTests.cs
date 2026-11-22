using System.Diagnostics;
using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class SingleInstanceForegroundTests
{
    [Theory]
    [InlineData(ActivationAction.Show)]
    [InlineData(ActivationAction.ViewSection)]
    public async Task ForegroundPermissionIsAttemptedForTheConnectedOwnerBeforeDispatch(ActivationAction action)
    {
        var applicationId = "SnuWatcher.Tests." + Guid.NewGuid().ToString("N");
        var permissionReached = new TaskCompletionSource<PipeServerIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePermission = new ManualResetEventSlim();
        await using var owner = new SingleInstanceCoordinator(applicationId);
        await using var second = new SingleInstanceCoordinator(applicationId, pipe =>
        {
            Assert.True(ForegroundPermissionTransfer.TryGetServerIdentity(pipe, out var identity));
            permissionReached.TrySetResult(identity);
            Assert.True(releasePermission.Wait(TimeSpan.FromSeconds(3)));
            return true;
        });
        Assert.True(owner.TryAcquire());
        Assert.False(second.TryAcquire());
        owner.StartListening(request => received.TrySetResult(request));

        var forwarding = Task.Run(() => second.ForwardAsync(new ActivationRequest(action), CancellationToken.None));
        try
        {
            var identity = await permissionReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal((uint)Environment.ProcessId, identity.ProcessId);
            Assert.Equal((uint)Process.GetCurrentProcess().SessionId, identity.SessionId);
            var first = await Task.WhenAny(received.Task, Task.Delay(150));
            Assert.NotSame(received.Task, first);
            releasePermission.Set();

            Assert.Equal(Environment.ProcessId, await forwarding);
            Assert.Equal(action, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Action);
        }
        finally { releasePermission.Set(); }
    }

    [Theory]
    [InlineData(ActivationAction.Quit)]
    [InlineData(ActivationAction.OpenRegistration)]
    public async Task NonWindowActionsDoNotRequestForeground(ActivationAction action)
    {
        var applicationId = "SnuWatcher.Tests." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var owner = new SingleInstanceCoordinator(applicationId);
        await using var second = new SingleInstanceCoordinator(applicationId, _ =>
        {
            Interlocked.Increment(ref calls);
            return true;
        });
        Assert.True(owner.TryAcquire());
        Assert.False(second.TryAcquire());
        owner.StartListening(request => received.TrySetResult(request));

        Assert.Equal(Environment.ProcessId,
            await second.ForwardAsync(new ActivationRequest(action), CancellationToken.None));
        Assert.Equal(action, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Action);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DeniedPermissionStillShowsTheExistingInstanceAndReportsTheLimit()
    {
        var applicationId = "SnuWatcher.Tests." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new SingleInstanceCoordinator(applicationId);
        await using var second = new SingleInstanceCoordinator(applicationId, _ => false);
        second.Diagnostic += message => diagnostic.TrySetResult(message);
        Assert.True(owner.TryAcquire());
        Assert.False(second.TryAcquire());
        owner.StartListening(request => received.TrySetResult(request));

        Assert.Equal(Environment.ProcessId,
            await second.ForwardAsync(new ActivationRequest(ActivationAction.Show), CancellationToken.None));
        Assert.Equal(ActivationAction.Show, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Action);
        Assert.Equal("Windows did not grant foreground permission; select SNU Watcher on the taskbar if needed.",
            await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
