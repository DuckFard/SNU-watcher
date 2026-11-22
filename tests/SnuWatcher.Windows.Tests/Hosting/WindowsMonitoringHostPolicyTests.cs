using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Windows.Hosting;
using Xunit;

namespace SnuWatcher.Windows.Tests.Hosting;

public sealed class WindowsMonitoringHostPolicyTests
{
    [Fact]
    public async Task FailedCanonicalPolicyFlushRetainsSourceStoreAndOwnerUntilAnExplicitRetry()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new HostTestSource();
        var diskUnavailable = 1;
        var pendingPolicy = 0;
        var recoverCalls = 0;
        SqliteWatchStore? store = null;
        var factories = new WindowsMonitoringHostFactories(async (profile, clock, ct) =>
        {
            store = await SqliteWatchStore.OpenAsync(new(profile.DatabasePath), clock, cancellationToken: ct);
            return store;
        }, (_, _, _) => Task.FromResult(new WindowsMonitoringHostSource(source,
            () => ValueTask.CompletedTask, source.DisposeAsync,
            () => Volatile.Read(ref pendingPolicy) != 0 ? "source_policy_storage_failed" : null,
            token =>
            {
                Assert.False(token.CanBeCanceled);
                Interlocked.Increment(ref recoverCalls);
                if (Volatile.Read(ref diskUnavailable) != 0) throw new IOException("Private injected pending-policy failure.");
                Volatile.Write(ref pendingPolicy, 0);
                return Task.CompletedTask;
            }, (_, token) =>
            {
                Assert.False(token.CanBeCanceled);
                Volatile.Write(ref pendingPolicy, 1);
                return Task.FromException<SourceAccessResumeResult>(new IOException("Private injected policy result failure."));
            })));
        var result = await WindowsMonitoringHost.OpenForTestingAsync(fixture.Profile, new HostTestChannel(), factories);
        var host = result.Host!;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => host.ResumeSourceAccessAsync(0));
            var blocked = await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(blocked.IsFinalized);
            Assert.Equal("source_policy_flush_failed", blocked.FailureCode);
            Assert.False(source.Disposed);
            Assert.NotNull(store);
            Assert.Empty(await store!.ListSubscriptionsAsync(default));
            var contender = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
            Assert.Equal(DatabaseOwnerDisposition.AlreadyOwned, contender.Disposition);
            Volatile.Write(ref diskUnavailable, 0);
            Assert.True((await host.RetryFinalizeStopAsync()).IsFinalized);
            Assert.True(source.Disposed);
            Assert.True(Volatile.Read(ref recoverCalls) >= 2);
            var replacement = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
            Assert.Equal(DatabaseOwnerDisposition.Acquired, replacement.Disposition);
            await replacement.Lease!.DisposeAsync();
        }
        finally
        {
            Volatile.Write(ref diskUnavailable, 0);
            if (host.State == WindowsMonitoringHostState.StopBlocked) await host.RetryFinalizeStopAsync();
            await host.DisposeAsync();
        }
    }
}
