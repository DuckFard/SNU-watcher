using System.Net;
using System.Net.Http.Headers;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Snu;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuStoredPolicyTests
{
    [Fact]
    public async Task ProductionFactoryUsesTheSuppliedPolicyStoreWithoutOpeningAnyNetworkRequest()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        await using var source = await SnuCourseSource.CreateAsync(fixture.Probe, timeProvider: fixture.Clock);
        Assert.Equal(1, fixture.Probe.ReadCalls);
        Assert.Throws<InvalidOperationException>(() => source.ResumeSourceAccess());
        await source.RecoverSourcePolicyAsync();
        Assert.Equal(2, fixture.Probe.ReadCalls);
        Assert.Equal(0, fixture.HttpCalls);
        Assert.Equal(0, (await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang)).Progress.LastIssuedOrdinal);
    }

    [Fact]
    public async Task UnknownIntentRecoveryFloorNeverShortensALongerPersistedServerCooldown()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        var one = (await fixture.Store.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Catalog))).Issued!;
        await fixture.Store.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Detail));
        await fixture.Store.RecordSourceRequestResultAsync(new(one.Token, SourceRequestOutcome.RateLimited, fixture.Clock.GetUtcNow(),
            receivedAtUtc: fixture.Clock.GetUtcNow(), retryAfter: SourceRetryAfter.Delta(TimeSpan.FromHours(5))));
        await fixture.ReopenAsync();
        await using var source = await fixture.SourceAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = source.GetCatalogAsync(2026, cancellationToken: cancellation.Token);
        await fixture.Clock.NextTimerAsync(TimeSpan.FromHours(5));
        Assert.Equal("source_recovering", source.SourcePolicyDiagnosticCode);
        Assert.Equal(0, fixture.HttpCalls);
        Assert.Empty(fixture.Probe.Commands);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task FactoryRequiresStoreAndHealthyStartupOwnsAndDisposesItsClient()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => SnuCourseSource.CreateAsync(null!));
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        fixture.Probe.FailRead = true;
        var handler = new StoredHandler((_, _) => Task.FromResult(StoredPolicyFixture.Json()));
        var error = await Assert.ThrowsAnyAsync<IOException>(() => SnuCourseSource.CreateStoredForTestingAsync(
            new HttpClient(handler), fixture.Probe, new(TimeSpan.Zero), fixture.Clock));
        Assert.DoesNotContain("private", error.Message);
        Assert.True(handler.Disposed);
        Assert.Equal(0, fixture.HttpCalls);
    }

    [Fact]
    public async Task FactoryAppliesInitialGapAndEachPhysicalRequestPersistsBeforeHttpWithoutHoldingSqlTransaction()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        await using var source = await fixture.SourceAsync(async (_, _) =>
        {
            var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang, default);
            Assert.Equal(fixture.HttpCalls, policy.Progress.LastIssuedOrdinal);
            Assert.Equal(fixture.HttpCalls - 1, policy.Progress.LatestCompletedOrdinal);
            return StoredPolicyFixture.Json();
        });
        var first = source.GetCatalogAsync(2026);
        Assert.Equal(0, fixture.HttpCalls);
        await fixture.AfterStartupAsync(first);
        await source.GetCatalogAsync(2026);
        Assert.Equal(2, fixture.Probe.Commands.Count);
        Assert.Equal(new long[] { 1, 2 }, fixture.Probe.Commands.Select(c => c.Token.Ordinal));
        Assert.All(fixture.Probe.Commands, command =>
        {
            Assert.Equal(SourceRequestOutcome.Success, command.Outcome);
            Assert.NotNull(command.ActualStartedAtUtc);
            Assert.NotNull(command.ReceivedAtUtc);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Long429SurvivesReopenAndLateCallerCancellationStillRecordsWithNone(bool httpDate, bool cleanupThrows)
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await using (var source = await fixture.SourceAsync((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new CancelOnDispose(cancellation, cleanupThrows) };
            response.Headers.RetryAfter = httpDate ? new RetryConditionHeaderValue(fixture.Clock.GetUtcNow().AddHours(3)) :
                new RetryConditionHeaderValue(TimeSpan.FromHours(3));
            return Task.FromResult(response);
        }))
        {
            var read = source.GetCatalogAsync(2026, cancellationToken: cancellation.Token);
            await Assert.ThrowsAsync<SnuSourceException>(() => fixture.AfterStartupAsync(read));
            Assert.True(cancellation.IsCancellationRequested);
            var result = Assert.Single(fixture.Probe.Commands);
            Assert.Equal(SourceRequestOutcome.RateLimited, result.Outcome);
            Assert.Equal(SourceRetryAfterKind.ResolvedDelay, result.RetryAfter!.Kind);
            Assert.Equal(TimeSpan.FromHours(3), result.RetryAfter.DeltaDelay);
            Assert.False(Assert.Single(fixture.Probe.RecordTokensCancelable));
        }
        await fixture.ReopenAsync();
        await using var reopened = await fixture.SourceAsync();
        using var pendingCancellation = new CancellationTokenSource();
        var pending = reopened.GetCatalogAsync(2026, cancellationToken: pendingCancellation.Token);
        await fixture.Clock.NextTimerAsync(TimeSpan.FromHours(3));
        Assert.Equal(1, fixture.HttpCalls);
        pendingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Restored403NeedsSuccessfulResumeCasAndSyncResumeCannotBypassHostedPolicy()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        await using (var source = await fixture.SourceAsync((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))))
            await Assert.ThrowsAsync<SnuSourceException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        await fixture.ReopenAsync();
        await using var restored = await fixture.SourceAsync();
        await Assert.ThrowsAsync<SnuSourceException>(() => restored.GetCatalogAsync(2026));
        Assert.Throws<InvalidOperationException>(() => restored.ResumeSourceAccess());
        var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang, default);
        Assert.False((await restored.ResumeSourceAccessAsync(policy.Revision - 1)).Applied);
        Assert.True((await restored.ResumeSourceAccessAsync(policy.Revision)).Applied);
        Assert.Equal(1, fixture.HttpCalls);
        await fixture.AfterStartupAsync(restored.GetCatalogAsync(2026));
        Assert.Equal(2, fixture.HttpCalls);
    }

    [Fact]
    public async Task CancelAfterIssuedIntentBeforeSendCreatesOnlyUnknownIntentAndLocalRecoveryFloor()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Probe.AfterIssue = cancellation.Cancel;
        await using (var source = await fixture.SourceAsync())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026, cancellationToken: cancellation.Token)));
        Assert.Equal(0, fixture.HttpCalls);
        Assert.Empty(fixture.Probe.Commands);
        fixture.Probe.AfterIssue = null;
        await fixture.ReopenAsync();
        await using var reopened = await fixture.SourceAsync();
        Assert.Equal("source_recovering", reopened.SourcePolicyDiagnosticCode);
        var next = reopened.GetCatalogAsync(2026);
        await fixture.Clock.NextTimerAsync(TimeSpan.FromSeconds(60));
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, fixture.HttpCalls);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(reopened.SourcePolicyDiagnosticCode);
    }

    [Fact]
    public async Task IssueStorageFailureSendsNothingAndRequiresHealthyExplicitRecovery()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        await using var source = await fixture.SourceAsync();
        fixture.Probe.FailIssue = true;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        Assert.Equal(0, fixture.HttpCalls);
        Assert.Equal("source_policy_storage_failed", source.SourcePolicyDiagnosticCode);
        fixture.Probe.FailIssue = false;
        await Assert.ThrowsAnyAsync<IOException>(() => source.GetCatalogAsync(2026));
        fixture.Probe.FailRead = true;
        await Assert.ThrowsAnyAsync<IOException>(() => source.RecoverSourcePolicyAsync());
        Assert.Equal("source_policy_storage_failed", source.SourcePolicyDiagnosticCode);
        fixture.Probe.FailRead = false;
        await source.RecoverSourcePolicyAsync();
        await source.GetCatalogAsync(2026);
        Assert.Equal(1, fixture.HttpCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedResultAcknowledgementReplaysTheSameFrozenCommandWithoutNewHttpOrToken(bool alreadyCommitted)
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        fixture.Probe.FailRecordBefore = !alreadyCommitted;
        fixture.Probe.FailRecordAfter = alreadyCommitted;
        await using var source = await fixture.SourceAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        var original = Assert.Single(fixture.Probe.Commands);
        Assert.Equal(1, fixture.HttpCalls);
        await Assert.ThrowsAnyAsync<IOException>(() => source.GetCatalogAsync(2026));
        await Assert.ThrowsAnyAsync<IOException>(() => source.ResumeSourceAccessAsync(0));
        fixture.Probe.FailRecordBefore = false;
        fixture.Probe.FailRecordAfter = false;
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await source.RecoverSourcePolicyAsync();
        Assert.Equal(2, fixture.Probe.Commands.Count);
        Assert.Same(original, fixture.Probe.Commands[1]);
        Assert.Equal(1, fixture.HttpCalls);
        var state = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang, default);
        Assert.Equal(1, state.Progress.LastIssuedOrdinal);
        Assert.Equal(1, state.Progress.LatestCompletedOrdinal);
    }

    private sealed class CancelOnDispose(CancellationTokenSource cancellation, bool cleanupThrows) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { cancellation.Cancel(); if (cleanupThrows) throw new OperationCanceledException(cancellation.Token); }
        }
    }
}
