using Xunit;
using static SnuWatcher.Application.Tests.Monitoring.Scheduling.MonitorFixture;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class RuntimeChannelWakeTests
{
    [Fact]
    public async Task ChannelWakeAdmissionClosesWithTheSessionAndDoesNotIssueSourceWork()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture();
        Assert.False(fixture.Session.NotifyDeliveryChannelChanged());
        await fixture.Session.StartAsync();
        await EventuallyAsync(() => fixture.Work.Reads > 0);
        for (var index = 0; index < 16; index++)
            _ = fixture.Session.NotifyDeliveryChannelChanged(); // A full signal queue may coalesce the wake.

        // With no tracked pending sections, a channel wake need not rediscover an empty ledger.
        Assert.Empty(fixture.Source.SearchCalls);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.Null(fixture.Session.Failure);
        Assert.True((await fixture.Session.StopAsync()).IsFinalized);
        Assert.False(fixture.Session.NotifyDeliveryChannelChanged());
    }

    [Fact]
    public async Task DisabledDeliveryNeverAdmitsAChannelWakeOrStartsDeliveryWork()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture(deliveryEnabled: false);
        await fixture.Session.StartAsync();

        Assert.False(fixture.Session.NotifyDeliveryChannelChanged());
        Assert.Equal(0, fixture.Work.Reads);
        Assert.Empty(fixture.Source.SearchCalls);
        Assert.True((await fixture.Session.StopAsync()).IsFinalized);
        Assert.False(fixture.Session.NotifyDeliveryChannelChanged());
    }
}
