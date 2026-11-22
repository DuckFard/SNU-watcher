using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Windows.Hosting;
using Xunit;

namespace SnuWatcher.Windows.Tests.Hosting;

public sealed class WindowsMonitoringHostTests
{
    [Fact]
    public async Task OwnerDeniedNeverInvokesTheStoreOrSourceFactory()
    {
        using var fixture = new RuntimeProfileFixture();
        var acquired = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
        await using var lease = acquired.Lease!;
        var stores = 0; var sources = 0;
        var factories = new WindowsMonitoringHostFactories((_, _, _) =>
        { Interlocked.Increment(ref stores); throw new InvalidOperationException("Store factory must not run."); }, (_, _, _) =>
        { Interlocked.Increment(ref sources); throw new InvalidOperationException("Source factory must not run."); });

        var result = await WindowsMonitoringHost.OpenForTestingAsync(fixture.Profile, new HostTestChannel(), factories);

        Assert.Equal(WindowsHostOpenDisposition.AlreadyOwned, result.Disposition);
        Assert.Null(result.Host);
        Assert.Equal(0, stores); Assert.Equal(0, sources);
        Assert.False(File.Exists(fixture.Profile.DatabasePath));
    }

    [Fact]
    public async Task SourceCreationFailureClosesTheStoreBeforeOwnerHandoff()
    {
        using var fixture = new RuntimeProfileFixture();
        SqliteWatchStore? opened = null;
        var factories = new WindowsMonitoringHostFactories(async (profile, clock, ct) =>
        {
            opened = await SqliteWatchStore.OpenAsync(new(profile.DatabasePath), clock, cancellationToken: ct);
            return opened;
        }, (_, _, _) => throw new IOException("Injected private startup failure."));

        await Assert.ThrowsAsync<IOException>(() => WindowsMonitoringHost.OpenForTestingAsync(fixture.Profile, new HostTestChannel(), factories));

        Assert.NotNull(opened);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => opened!.ListSubscriptionsAsync(default));
        var next = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
        Assert.Equal(DatabaseOwnerDisposition.Acquired, next.Disposition);
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task EmptyHostRunsWithoutSourceCallsAndStopReleasesItsPrivateOwner()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new HostTestSource();
        var opened = await WindowsMonitoringHost.OpenValidationAsync(fixture.Profile, () => source, new HostTestChannel());
        Assert.Equal(WindowsHostOpenDisposition.Started, opened.Disposition);
        var host = opened.Host!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (host.Session.LatestSnapshot is null) await Task.Delay(10, deadline.Token);
            Assert.Equal(0, source.Calls);
            Assert.Equal(WindowsMonitoringHostState.Running, host.State);
            Assert.True(host.NotifyDeliveryChannelChanged());
            Assert.True((await host.StopAsync()).IsFinalized);
            Assert.False(host.NotifyDeliveryChannelChanged());
            Assert.True(source.Disposed);
            var replacement = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
            Assert.Equal(DatabaseOwnerDisposition.Acquired, replacement.Disposition);
            await replacement.Lease!.DisposeAsync();
        }
        finally { await host.DisposeAsync(); }
    }

    [Fact]
    public async Task AlreadyOwnedValidationDoesNotCreateOrDisposeTheCallersFakeSource()
    {
        using var fixture = new RuntimeProfileFixture();
        var owned = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
        await using var lease = owned.Lease!;
        var calls = 0;
        var result = await WindowsMonitoringHost.OpenValidationAsync(fixture.Profile, () =>
        { Interlocked.Increment(ref calls); return new HostTestSource(); }, new HostTestChannel());
        Assert.Null(result.Host);
        Assert.Equal(0, calls);
    }
}

internal sealed class HostTestChannel : IBatchDesktopAlertChannel
{
    internal int Calls;
    public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(BatchChannelReadiness.Ready); }
    public Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken cancellationToken)
    { Interlocked.Increment(ref Calls); return Task.FromResult(new BatchChannelResult(BatchChannelOutcome.OsAccepted)); }
}
