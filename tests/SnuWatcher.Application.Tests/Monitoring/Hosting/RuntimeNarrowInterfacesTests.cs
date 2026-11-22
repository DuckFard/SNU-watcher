using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class RuntimeNarrowInterfacesTests
{
    [Fact]
    public async Task NarrowWatchAndProfileViewsUseSameAdmissionReceiptsAndStopBoundary()
    {
        await using var fixture = new InteractiveRuntimeFixture();
        var watches = Assert.IsAssignableFrom<IRuntimeWatchCommands>(fixture.Session);
        var profile = Assert.IsAssignableFrom<IRuntimeRefreshProfile>(fixture.Session);
        await fixture.Session.StartAsync();
        var command = new NewSubscription(Guid.NewGuid(), "통계학", SubscriptionKind.Section,
            [MonitorFixture.Key()], false, null, MonitorFixture.Now);
        var receipt = ScriptedRuntimeMutations.Receipt(command);
        fixture.Mutations.Create = (actual, _) =>
        {
            Assert.Same(command, actual);
            return Task.FromResult(receipt);
        };
        Assert.Same(receipt, await watches.CreateAsync(command));
        Assert.Equal(WatchRefreshDisposition.NotWatched,
            (await watches.RefreshAsync(MonitorFixture.Key())).MonitorResult!.Disposition);
        await Assert.ThrowsAsync<StoreVersionConflictException>(() => watches.ChangeAsync(
            new PauseSubscription(command.Id, receipt.DefinitionRevision, MonitorFixture.Now)));
        Assert.Same(fixture.Session.AppliedRefreshProfile, profile.AppliedRefreshProfile);
        Assert.Same(await profile.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1), fixture.Session.AppliedRefreshProfile);
        await fixture.Session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => watches.CreateAsync(command));
        await Assert.ThrowsAsync<InvalidOperationException>(() => profile.SetRefreshProfileAsync(WatchMonitorProfile.Normal, 2));
        Assert.Equal(1, fixture.Mutations.Creates);
        Assert.Null(fixture.Session.Failure);
    }
}
