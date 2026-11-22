using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class MonitoringRuntimeSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task EmptyLifetimeOwnsControlsAndNotWatchedRefreshThenClosesAdmissionWithoutSourceWork()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture();
        var session = fixture.Session;
        Assert.Equal(RuntimeSessionState.Created, session.State);
        await session.StartAsync();
        await MonitorFixture.EventuallyAsync(() => session.LatestSnapshot is not null);
        Assert.Equal(RuntimeSessionState.Running, session.State);
        await session.SetOnlineAsync(false);
        await session.SetSuspendedAsync(true);
        await MonitorFixture.EventuallyAsync(() => session.LatestSnapshot is { IsOnline: false, IsSuspended: true });
        await session.SetOnlineAsync(true);
        await session.SetSuspendedAsync(false);
        await MonitorFixture.EventuallyAsync(() => session.LatestSnapshot is { IsOnline: true, IsSuspended: false });
        var refresh = await session.RefreshAsync(MonitorFixture.Key()).WaitAsync(Timeout);
        Assert.Equal(OwnedRefreshDisposition.Completed, refresh.Disposition);
        Assert.Equal(WatchRefreshDisposition.NotWatched, refresh.MonitorResult!.Disposition);
        var update = await session.Updates.ReadAsync().AsTask().WaitAsync(Timeout);
        Assert.NotNull(update.Snapshot);
        Assert.Null(session.Failure);
        Assert.False(session.FailureTask.IsCompleted);
        var stop = session.StopAsync();
        Assert.Same(stop, session.StopAsync());
        var result = await stop.WaitAsync(Timeout);
        Assert.True(result.IsFinalized);
        Assert.Empty(result.PendingKnownSections);
        Assert.Null(result.Failure);
        Assert.Equal(RuntimeSessionState.Stopped, session.State);
        Assert.True((await session.RetryFinalizeStopAsync()).IsFinalized);
        Assert.False(session.Wake());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetOnlineAsync(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        Assert.Empty(fixture.Source.CatalogCalls);
        Assert.Empty(fixture.Source.SearchCalls);
        Assert.Empty(fixture.Channel.Envelopes);
    }

    [Fact]
    public async Task UnexpectedMonitorDependencyFaultLatchesSanitizedFailureAndStopsEveryActor()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture();
        fixture.Store.ListFailure = new IOException("private database path");
        await fixture.Session.StartAsync();
        var failure = await fixture.Session.FailureTask.WaitAsync(Timeout);
        Assert.Equal(RuntimeFailureKind.Actor, failure.Kind);
        Assert.Equal("monitor_failed", failure.Code);
        Assert.DoesNotContain("private", failure.Code);
        Assert.True((await fixture.Session.StopAsync().WaitAsync(Timeout)).IsFinalized);
        Assert.Equal(RuntimeSessionState.Stopped, fixture.Session.State);
        Assert.Empty(fixture.Source.SearchCalls);
    }

    [Fact]
    public async Task UnexpectedCommandStorageFailureIsStickyBeforeReturningSanitizedCallerError()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture();
        fixture.Mutations.Create = (_, _) => throw new IOException("private store exception");
        await fixture.Session.StartAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.CreateAsync(NewWatch()));
        Assert.DoesNotContain("private", failure.Message);
        Assert.True(fixture.Session.FailureTask.IsCompleted);
        Assert.Equal("runtime_command_failed", (await fixture.Session.FailureTask).Code);
        Assert.Equal(RuntimeFailureKind.SavedState, fixture.Session.Failure!.Kind);
        Assert.True((await fixture.Session.StopAsync().WaitAsync(Timeout)).IsFinalized);
        Assert.Equal(1, fixture.Mutations.Creates);
    }

    [Fact]
    public async Task ExpectedRevisionConflictIsObservedWithoutTerminatingHealthyRuntime()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture();
        await fixture.Session.StartAsync();
        await Assert.ThrowsAsync<StoreVersionConflictException>(() => fixture.Session.ChangeAsync(
            new PauseSubscription(Guid.NewGuid(), 1, fixture.Clock.GetUtcNow())));
        await fixture.Session.SetSuspendedAsync(true);
        Assert.Equal(RuntimeSessionState.Running, fixture.Session.State);
        Assert.False(fixture.Session.FailureTask.IsCompleted);
        Assert.Null(fixture.Session.Failure);
        Assert.True((await fixture.Session.StopAsync()).IsFinalized);
    }

    [Fact]
    public async Task AdmittedLateMutationIsDrainedAndItsActualReceiptSurvivesShutdown()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource<SubscriptionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Mutations.Create = (_, _) => { entered.SetResult(); return commit.Task; };
        await fixture.Session.StartAsync();
        var command = NewWatch();
        var receipt = ScriptedRuntimeMutations.Receipt(command);
        var creating = fixture.Session.CreateAsync(command);
        Task<RuntimeSessionStopResult>? stopping = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            stopping = fixture.Session.StopAsync();
            Assert.False(stopping.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.CreateAsync(NewWatch()));
        }
        finally { commit.TrySetResult(receipt); }
        Assert.Same(receipt, await creating.WaitAsync(Timeout));
        Assert.True((await stopping!.WaitAsync(Timeout)).IsFinalized);
        Assert.Equal(1, fixture.Mutations.Creates);
    }

    [Fact]
    public async Task StartupRecoveryFailureIsFailStopAndNeverStartsSourceOrDelivery()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture();
        fixture.Recovery.Read = _ => throw new IOException("private recovery file");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.StartAsync());
        Assert.DoesNotContain("private", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal("runtime_startup_failed", (await fixture.Session.FailureTask).Code);
        Assert.True((await fixture.Session.StopAsync().WaitAsync(Timeout)).IsFinalized);
        Assert.Empty(fixture.Source.CatalogCalls);
        Assert.Empty(fixture.Channel.Envelopes);
    }

    [Fact]
    public async Task StopBeforeStartCannotRunRecoveryAndRetryCannotLaunchActors()
    {
        await using var fixture = new MonitoringRuntimeSessionFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.RetryFinalizeStopAsync());
        var stopped = await fixture.Session.StopAsync();
        Assert.True(stopped.IsFinalized);
        Assert.True((await fixture.Session.RetryFinalizeStopAsync()).IsFinalized);
        Assert.Equal(0, fixture.Recovery.Reads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.StartAsync());
        Assert.Null(fixture.Session.LatestSnapshot);
        Assert.Empty(fixture.Source.CatalogCalls);
    }

    private static NewSubscription NewWatch() => new(Guid.NewGuid(), "Statistics", SubscriptionKind.Section,
        [MonitorFixture.Key()], false, null, MonitorFixture.Now);
}
