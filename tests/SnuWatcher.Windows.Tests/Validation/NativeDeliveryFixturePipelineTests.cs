using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Core;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Validation;
using Xunit;

namespace SnuWatcher.Windows.Tests.Validation;

/// <summary>Offline pipeline integration only: its explicit fake channel does not establish native acceptance.</summary>
public sealed class NativeDeliveryFixturePipelineTests
{
    [Fact]
    public async Task RealParserGatewayAndHostCommitFullBeforeOpeningAndRetainOneUnchangedReceiptAcrossReopen()
    {
        var profile = RuntimeProfile.CreateValidationRun(Guid.NewGuid());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        var channel = new OfflineAcceptedChannel();
        WindowsMonitoringHost? host = null;
        try
        {
            var first = new NativeDeliveryFixtureTransport();
            host = (await WindowsMonitoringHost.OpenOfflineSnuValidationAsync(profile, () => first, channel,
                options: new RuntimeSessionOptions(monitor: new(jitterPercent: 0)), cancellationToken: timeout.Token)).Host!;
            var saved = await host.Preferences.TrySaveAsync(new(Guid.NewGuid(), 0, new(UiLanguage.English, 2026,
                new(2026, "U000200002U000300002"), WatchMonitorProfile.Faster, UiCloseBehavior.Quit, 1)), timeout.Token);
            await host.RefreshProfile.SetRefreshProfileAsync(WatchMonitorProfile.Faster, saved.CurrentSnapshot.Revision, timeout.Token);
            var id = Guid.NewGuid();
            await host.Session.CreateAsync(new(id, "TEST offline fixture", SubscriptionKind.Section,
                [NativeDeliveryFixtureTransport.Key], false, null, DateTimeOffset.UtcNow), timeout.Token);
            await UntilAsync(async () => (await host.Dashboard.ReadWatchDetailsAsync(id, timeout.Token))?.Sections.Single().Baseline?.Seats == new SeatCount(40, 40), timeout.Token);
            Assert.Empty((await host.Alerts.ReadPageAsync(cancellationToken: timeout.Token)).Items);
            Assert.Equal(0, channel.Calls);
            first.OpenOneSeat();
            await host.Session.RefreshAsync(NativeDeliveryFixtureTransport.Key, timeout.Token);
            await UntilAsync(async () => (await host.Alerts.ReadPageAsync(cancellationToken: timeout.Token)).Items is [{ Delivery.Status: DeliveryStatus.OsAccepted }], timeout.Token);
            var receipt = (await host.Alerts.ReadPageAsync(cancellationToken: timeout.Token)).Items.Single();
            Assert.Equal(1, receipt.Trigger.CurrentSeats);
            Assert.Equal(0, receipt.Trigger.PreviousSeats);
            Assert.Equal(1, receipt.Delivery.Attempts);
            Assert.Equal(1, channel.Calls);
            Assert.True((await host.StopAsync()).IsFinalized);
            await host.DisposeAsync(); host = null;
            Assert.True(first.IsDisposed);
            var reopenedTransport = new NativeDeliveryFixtureTransport(); reopenedTransport.OpenOneSeat();
            host = (await WindowsMonitoringHost.OpenOfflineSnuValidationAsync(profile, () => reopenedTransport, channel,
                options: new RuntimeSessionOptions(monitor: new(jitterPercent: 0)), cancellationToken: timeout.Token)).Host!;
            await host.Session.RefreshAsync(NativeDeliveryFixtureTransport.Key, timeout.Token);
            await UntilAsync(async () => (await host.Dashboard.ReadWatchDetailsAsync(id, timeout.Token))?.Sections.Single().Baseline?.AttemptSequence > receipt.Trigger.Ordinal, timeout.Token);
            var retained = (await host.Alerts.ReadPageAsync(cancellationToken: timeout.Token)).Items.Single();
            Assert.Equal(receipt, retained);
            Assert.True(reopenedTransport.OpenSearches > 0);
            Assert.Equal(1, channel.Calls);
        }
        finally
        {
            if (host is not null) { Assert.True((await host.StopAsync()).IsFinalized); await host.DisposeAsync(); }
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher-runtime-validation"));
            Assert.Equal(parent, Path.GetDirectoryName(Path.GetFullPath(profile.DirectoryPath)));
            Directory.Delete(profile.DirectoryPath, true);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate, CancellationToken ct)
    { while (!await predicate()) await Task.Delay(100, ct); }

    [Fact]
    public async Task DisabledReadinessLeavesRealOpeningPendingWithoutReservationOrBudgetCharge()
    {
        var profile = RuntimeProfile.CreateValidationRun(Guid.NewGuid());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var source = new NativeDeliveryFixtureTransport();
        var channel = new OfflineDisabledChannel();
        WindowsMonitoringHost? host = null;
        try
        {
            host = (await WindowsMonitoringHost.OpenOfflineSnuValidationAsync(profile, () => source, channel,
                options: new RuntimeSessionOptions(monitor: new(jitterPercent: 0)), cancellationToken: timeout.Token)).Host!;
            var id = Guid.NewGuid();
            await host.Session.CreateAsync(new(id, "TEST disabled fixture", SubscriptionKind.Section,
                [NativeDeliveryFixtureTransport.Key], false, null, DateTimeOffset.UtcNow), timeout.Token);
            await UntilAsync(async () => (await host.Dashboard.ReadWatchDetailsAsync(id, timeout.Token))?.Sections.Single().Baseline?.Seats == new SeatCount(40, 40), timeout.Token);
            Assert.Empty((await host.Alerts.ReadPageAsync(cancellationToken: timeout.Token)).Items);
            source.OpenOneSeat(); channel.Opening = true;
            await host.Session.RefreshAsync(NativeDeliveryFixtureTransport.Key, timeout.Token);
            await channel.OpeningRead.Task.WaitAsync(timeout.Token);
            var opening = (await host.Alerts.ReadPageAsync(cancellationToken: timeout.Token)).Items.Single();
            Assert.Equal(DeliveryStatus.Pending, opening.Delivery.Status);
            Assert.Equal(0, opening.Delivery.Attempts);
            Assert.Null(opening.Delivery.UnresolvedAttemptId);
            var attempts = await host.Alerts.ReadAttemptsAsync(opening.EventId, cancellationToken: timeout.Token);
            Assert.DoesNotContain(attempts!.Items, item => item.BatchAttempt is not null);
            Assert.Equal(0, channel.Calls);
            Assert.Equal(0, source.RejectedRequests);
        }
        finally
        {
            if (host is not null) { Assert.True((await host.StopAsync()).IsFinalized); await host.DisposeAsync(); }
            Assert.True(source.IsDisposed);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher-runtime-validation"));
            Assert.Equal(parent, Path.GetDirectoryName(Path.GetFullPath(profile.DirectoryPath)));
            Directory.Delete(profile.DirectoryPath, true);
        }
    }

    private sealed class OfflineDisabledChannel : IBatchDesktopAlertChannel
    {
        internal volatile bool Opening;
        internal int Calls;
        internal TaskCompletionSource OpeningRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken cancellationToken)
        { if (Opening) OpeningRead.TrySetResult(); return Task.FromResult(BatchChannelReadiness.Disabled); }
        public Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Disabled fixture channel must not be submitted."); }
    }

    private sealed class OfflineAcceptedChannel : IBatchDesktopAlertChannel
    {
        internal int Calls;
        public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(BatchChannelReadiness.Ready);
        public Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new BatchChannelResult(BatchChannelOutcome.OsAccepted)); }
    }
}
