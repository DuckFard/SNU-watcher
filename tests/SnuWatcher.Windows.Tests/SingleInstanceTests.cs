using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task ShutdownDoesNotNeedTheUiSynchronizationContext()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            try
            {
                var owner = new SingleInstanceCoordinator("SnuWatcher.Tests." + Guid.NewGuid().ToString("N"));
                Assert.True(owner.TryAcquire());
                owner.StartListening(_ => { });
                owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                finished.SetResult();
            }
            catch (Exception error) { finished.SetException(error); }
        }) { IsBackground = true };
        thread.Start();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SecondInstanceForwardsValidatedActionToExistingOwner()
    {
        var applicationId = "SnuWatcher.Tests." + Guid.NewGuid().ToString("N");
        await using var owner = new SingleInstanceCoordinator(applicationId);
        await using var second = new SingleInstanceCoordinator(applicationId);
        var received = new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(owner.TryAcquire());
        Assert.False(second.TryAcquire());
        owner.StartListening(action => received.TrySetResult(action));
        var acknowledgedOwner = await second.ForwardAsync(new ActivationRequest(ActivationAction.Show), CancellationToken.None);

        var action = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ActivationAction.Show, action.Action);
        Assert.Equal(Environment.ProcessId, acknowledgedOwner);
    }

    [Fact]
    public async Task OwnerExitReleasesProcessOwnership()
    {
        var applicationId = "SnuWatcher.Tests." + Guid.NewGuid().ToString("N");
        await using (var first = new SingleInstanceCoordinator(applicationId))
        {
            Assert.True(first.TryAcquire());
        }

        await using var replacement = new SingleInstanceCoordinator(applicationId);
        Assert.True(replacement.TryAcquire());
    }

    [Theory]
    [InlineData("..\\other-user")]
    [InlineData("bad/name")]
    [InlineData("")]
    public void RejectsInvalidIpcApplicationIdentifiers(string input)
    {
        Assert.Throws<ArgumentException>(() => new SingleInstanceCoordinator(input));
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }
}
