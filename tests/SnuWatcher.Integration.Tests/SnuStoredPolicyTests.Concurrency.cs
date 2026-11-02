using System.Net;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Snu;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuStoredPolicyConcurrencyTests
{
    [Fact]
    public async Task UtcRollbackDuringSuccessfulAdmissionRechecksTheDurableFloorBeforeAnySend()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        var previous = (await fixture.Store.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Catalog))).Issued!;
        await fixture.Store.RecordSourceRequestResultAsync(new(previous.Token, SourceRequestOutcome.RateLimited, fixture.Clock.GetUtcNow(),
            receivedAtUtc: fixture.Clock.GetUtcNow(), retryAfter: SourceRetryAfter.Delta(TimeSpan.FromSeconds(60))));
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        await using var source = await fixture.SourceAsync();
        fixture.Probe.AfterIssue = () => fixture.Clock.AdjustUtc(TimeSpan.FromHours(-1));
        var failure = await Assert.ThrowsAsync<SnuSourceException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        Assert.Equal(SourceFailureKind.RateLimited, failure.Kind);
        Assert.Equal(0, fixture.HttpCalls);
        Assert.Empty(fixture.Probe.Commands);
        var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang);
        Assert.Equal(2, policy.Progress.LastIssuedOrdinal);
        Assert.Equal(1, policy.Progress.LatestCompletedOrdinal);
    }

    [Fact]
    public async Task HeldKnownResultWriteKeepsNextHttpAndShutdownWaitingWithoutRefreshingItsFacts()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        var entered = StoredPolicyFixture.Barrier();
        var release = StoredPolicyFixture.Barrier();
        fixture.Probe.HoldRecord = async () => { entered.TrySetResult(); await release.Task; };
        var source = await fixture.SourceAsync();
        var first = source.GetCatalogAsync(2026);
        await fixture.Clock.NextTimerAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var original = Assert.Single(fixture.Probe.Commands);
        var next = source.GetCatalogAsync(2026);
        var shutdown = source.DisposeAsync().AsTask();
        try
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1, fixture.HttpCalls);
            Assert.False(first.IsCompleted);
            Assert.False(Assert.Single(fixture.Probe.RecordTokensCancelable));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
            Assert.Equal(original.ActualStartedAtUtc, original.ReceivedAtUtc);
            Assert.Equal(original.ReceivedAtUtc, original.CompletedAtUtc);
        }
        finally { release.TrySetResult(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        var stored = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang);
        Assert.Equal(original.CompletedAtUtc, stored.Progress.LastSuccessfulAtUtc);
    }

    [Fact]
    public async Task SuccessfulResumeCannotBeOverwrittenWhenTheOld403CallbackFinallyEscapes()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        var releasedScope = StoredPolicyFixture.Barrier();
        var returnToQueue = StoredPolicyFixture.Barrier();
        var scopes = 0;
        await using var source = await fixture.SourceAsync((_, _) => Task.FromResult(fixture.HttpCalls == 1
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) : StoredPolicyFixture.Json()), async () =>
        {
            if (Interlocked.Increment(ref scopes) == 1) { releasedScope.TrySetResult(); await returnToQueue.Task; }
        });
        var first = source.GetCatalogAsync(2026);
        await fixture.Clock.NextTimerAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await releasedScope.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang);
            Assert.True((await source.ResumeSourceAccessAsync(policy.Revision)).Applied);
            Assert.False(first.IsCompleted);
        }
        finally { returnToQueue.TrySetResult(); }
        await Assert.ThrowsAsync<SnuSourceException>(() => first);
        Assert.NotEmpty((await source.GetCatalogAsync(2026)).Terms);
        Assert.Equal(2, fixture.HttpCalls);
    }

    [Fact]
    public async Task SourceShutdownAwaitsActiveResumeAndRejectsQueuedControlsBeforeTheirDbWork()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        var entered = StoredPolicyFixture.Barrier();
        var release = StoredPolicyFixture.Barrier();
        var source = await fixture.SourceAsync((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<SnuSourceException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        fixture.Probe.HoldResume = async () => { entered.TrySetResult(); await release.Task; };
        var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang);
        var resume = source.ResumeSourceAccessAsync(policy.Revision);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedControl = source.RecoverSourcePolicyAsync();
        var shutdown = source.DisposeAsync().AsTask();
        try { Assert.False(shutdown.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.True((await resume.WaitAsync(TimeSpan.FromSeconds(5))).Applied);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedControl);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Probe.ReadCalls);
    }

    [Fact]
    public async Task SourceShutdownAlsoAwaitsAnActiveRecoveryReplayWithNoHttpInFlight()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        fixture.Probe.FailRecordAfter = true;
        var source = await fixture.SourceAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        fixture.Probe.FailRecordAfter = false;
        var entered = StoredPolicyFixture.Barrier();
        var release = StoredPolicyFixture.Barrier();
        fixture.Probe.HoldRecord = async () => { entered.TrySetResult(); await release.Task; };
        var recovery = source.RecoverSourcePolicyAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = source.DisposeAsync().AsTask();
        try { Assert.False(shutdown.IsCompleted); Assert.Equal(1, fixture.HttpCalls); }
        finally { release.TrySetResult(); }
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Probe.Commands.Count);
        Assert.All(fixture.Probe.RecordTokensCancelable, cancelable => Assert.False(cancelable));
    }

    [Fact]
    public async Task DurableGateAppearingAfterFactoryReadProducesNoNewHttpOrFabricatedResult()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        await using var source = await fixture.SourceAsync();
        var issued = (await fixture.Store.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Detail))).Issued!;
        await fixture.Store.RecordSourceRequestResultAsync(new(issued.Token, SourceRequestOutcome.AccessRestricted, fixture.Clock.GetUtcNow()));
        await Assert.ThrowsAsync<SnuSourceException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        Assert.Equal(0, fixture.HttpCalls);
        Assert.Empty(fixture.Probe.Commands);
        var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang);
        Assert.Equal(1, policy.Progress.LastIssuedOrdinal);
    }
}
