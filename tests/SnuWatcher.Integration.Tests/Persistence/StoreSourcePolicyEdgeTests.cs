using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreSourcePolicyEdgeTests
{
    private const string Source = SourcePolicyIds.SnuSugang;
    private static ISourcePolicyStore Policy(StoreFixture f) => f.Store;

    [Fact]
    public async Task FailedIntentWriteDoesNotConsumeOrdinalOrCreateDurableTransportEvidence()
    {
        var armed = false;
        await using var f = await StoreFixture.CreateAsync(stage =>
        { if (armed && stage == SqliteStoreWriteStage.SourceIssued) throw new EdgeFault(); });
        armed = true;
        await Assert.ThrowsAsync<EdgeFault>(() => IssueAsync(f));
        armed = false;
        await f.ReopenAsync();
        var empty = await Policy(f).ReadSourcePolicyAsync(Source, default);
        Assert.Equal(0, empty.Revision);
        Assert.Equal(0, empty.Progress.LastIssuedOrdinal);
        Assert.Null(empty.Progress.LastIssuedAtUtc);
        Assert.Equal(1, (await IssueAsync(f)).Token.Ordinal);
    }

    [Fact]
    public async Task FailedResultWriteRollsBackPolicyAndLedgerThenOriginalRequestCanBeRetried()
    {
        var armed = false;
        await using var f = await StoreFixture.CreateAsync(stage =>
        { if (armed && stage == SqliteStoreWriteStage.SourcePolicyResult) throw new EdgeFault(); });
        var issued = await IssueAsync(f);
        var before = await Policy(f).ReadSourcePolicyAsync(Source, default);
        var command = new SourceRequestResultCommand(issued.Token, SourceRequestOutcome.RateLimited,
            f.Clock.GetUtcNow(), receivedAtUtc: f.Clock.GetUtcNow(), retryAfter: SourceRetryAfter.Delta(TimeSpan.FromHours(2)));
        armed = true;
        await Assert.ThrowsAsync<EdgeFault>(() => Policy(f).RecordSourceRequestResultAsync(command, default));
        armed = false;
        await f.ReopenAsync();
        Assert.Equal(before, await Policy(f).ReadSourcePolicyAsync(Source, default));
        var receipt = await Policy(f).RecordSourceRequestResultAsync(command, default);
        Assert.True(receipt.PolicyApplied);
        var updated = await Policy(f).ReadSourcePolicyAsync(Source, default);
        Assert.Equal(before.Revision + 1, updated.Revision);
        Assert.Equal(issued.Token.Ordinal, updated.Progress.LatestCompletedOrdinal);
        Assert.NotNull(updated.Cooldown);
        await f.ReopenAsync();
        Assert.Equal(receipt, await Policy(f).RecordSourceRequestResultAsync(command, default));
        Assert.Equal(updated, await Policy(f).ReadSourcePolicyAsync(Source, default));
    }

    [Fact]
    public async Task FailedExplicitResumePreservesAccessPauseCooldownAndWatermarkAcrossReopen()
    {
        var armed = false;
        await using var f = await StoreFixture.CreateAsync(stage =>
        { if (armed && stage == SqliteStoreWriteStage.SourceAccessResume) throw new EdgeFault(); });
        var one = await IssueAsync(f); var two = await IssueAsync(f);
        await Policy(f).RecordSourceRequestResultAsync(new(one.Token, SourceRequestOutcome.RateLimited,
            f.Clock.GetUtcNow(), receivedAtUtc: f.Clock.GetUtcNow(), retryAfter: SourceRetryAfter.Delta(TimeSpan.FromHours(2))), default);
        await Policy(f).RecordSourceRequestResultAsync(new(two.Token, SourceRequestOutcome.AccessRestricted, f.Clock.GetUtcNow()), default);
        var paused = await Policy(f).ReadSourcePolicyAsync(Source, default);
        var command = new SourceAccessResumeCommand(Source, paused.Revision);
        armed = true;
        await Assert.ThrowsAsync<EdgeFault>(() => Policy(f).ResumeSourceAccessAsync(command, default));
        armed = false;
        await f.ReopenAsync();
        Assert.Equal(paused, await Policy(f).ReadSourcePolicyAsync(Source, default));
        var resumed = await Policy(f).ResumeSourceAccessAsync(command, default);
        Assert.True(resumed.Applied);
        Assert.Null(resumed.Policy.AccessPause);
        Assert.Equal(paused.Cooldown, resumed.Policy.Cooldown);
        Assert.Equal(two.Token.Ordinal, resumed.Policy.ResumeWatermark);
        Assert.False((await Policy(f).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default)).WasIssued);
    }

    [Fact]
    public async Task DelayedPersistenceUsesOriginalReceiptDeadlineAndUtcRollbackCannotEraseDurableGate()
    {
        await using var f = await StoreFixture.CreateAsync();
        var issued = await IssueAsync(f);
        var receiptAt = f.Clock.GetUtcNow();
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await Policy(f).RecordSourceRequestResultAsync(new(issued.Token, SourceRequestOutcome.RateLimited,
            f.Clock.GetUtcNow(), receivedAtUtc: receiptAt, retryAfter: SourceRetryAfter.Delta(TimeSpan.FromMinutes(1))), default);
        var policy = await Policy(f).ReadSourcePolicyAsync(Source, default);
        Assert.Equal(receiptAt.AddMinutes(1), policy.Cooldown!.NotBeforeUtc);
        Assert.True((await Policy(f).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default)).WasIssued);
        f.Clock.Set(receiptAt.AddSeconds(30));
        await f.ReopenAsync();
        Assert.False((await Policy(f).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Catalog), default)).WasIssued);
        Assert.Equal(policy.Cooldown, (await Policy(f).ReadSourcePolicyAsync(Source, default)).Cooldown);
    }

    [Fact]
    public async Task NewerInflightSuccessKeepsAccessPauseCooldownAndLastSuccessEvidenceSeparate()
    {
        await using var f = await StoreFixture.CreateAsync();
        var rate = await IssueAsync(f); var denied = await IssueAsync(f); var success = await IssueAsync(f);
        await Policy(f).RecordSourceRequestResultAsync(new(rate.Token, SourceRequestOutcome.RateLimited,
            f.Clock.GetUtcNow(), receivedAtUtc: f.Clock.GetUtcNow(), retryAfter: SourceRetryAfter.Delta(TimeSpan.FromHours(1))), default);
        await Policy(f).RecordSourceRequestResultAsync(new(denied.Token, SourceRequestOutcome.AccessRestricted, f.Clock.GetUtcNow()), default);
        var paused = await Policy(f).ReadSourcePolicyAsync(Source, default);
        await Policy(f).RecordSourceRequestResultAsync(new(success.Token, SourceRequestOutcome.Success, f.Clock.GetUtcNow()), default);
        await f.ReopenAsync();
        var current = await Policy(f).ReadSourcePolicyAsync(Source, default);
        Assert.Equal(SourceRequestOutcome.Success, current.Progress.LatestOutcome);
        Assert.Equal(success.Token.Ordinal, current.Progress.LastSuccessfulOrdinal);
        Assert.Equal(paused.AccessPause, current.AccessPause);
        Assert.Equal(paused.Cooldown, current.Cooldown);
        Assert.False((await Policy(f).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.SearchPage), default)).WasIssued);
    }

    [Fact]
    public async Task IgnoredOlderResultHasDurableReplayReceiptButCannotExtendNewerPolicy()
    {
        await using var f = await StoreFixture.CreateAsync();
        var older = await IssueAsync(f); var newer = await IssueAsync(f);
        await Policy(f).RecordSourceRequestResultAsync(new(newer.Token, SourceRequestOutcome.Success, f.Clock.GetUtcNow()), default);
        var current = await Policy(f).ReadSourcePolicyAsync(Source, default);
        var late = new SourceRequestResultCommand(older.Token, SourceRequestOutcome.RateLimited,
            f.Clock.GetUtcNow().AddDays(1), receivedAtUtc: f.Clock.GetUtcNow(), retryAfter: SourceRetryAfter.Delta(TimeSpan.FromDays(90)));
        var ignored = await Policy(f).RecordSourceRequestResultAsync(late, default);
        Assert.False(ignored.PolicyApplied);
        Assert.Equal(current, await Policy(f).ReadSourcePolicyAsync(Source, default));
        var third = await IssueAsync(f);
        await Policy(f).RecordSourceRequestResultAsync(new(third.Token, SourceRequestOutcome.Success, f.Clock.GetUtcNow()), default);
        await f.ReopenAsync();
        Assert.Equal(ignored, await Policy(f).RecordSourceRequestResultAsync(late, default));
        Assert.Null((await Policy(f).ReadSourcePolicyAsync(Source, default)).Cooldown);
    }

    [Fact]
    public async Task ResumeWithoutAPauseCannotPreemptivelySuppressAnInflightAccessRestriction()
    {
        await using var f = await StoreFixture.CreateAsync();
        var issued = await IssueAsync(f);
        var before = await Policy(f).ReadSourcePolicyAsync(Source, default);
        var noop = await Policy(f).ResumeSourceAccessAsync(new(Source, before.Revision), default);
        Assert.True(noop.Applied);
        Assert.Equal(before, noop.Policy);
        await Policy(f).RecordSourceRequestResultAsync(new(issued.Token, SourceRequestOutcome.AccessRestricted, f.Clock.GetUtcNow()), default);
        Assert.NotNull((await Policy(f).ReadSourcePolicyAsync(Source, default)).AccessPause);
    }

    [Fact]
    public async Task ConcurrentResumeAcrossStoresHasOneCasWinnerAndRetainsCooldown()
    {
        await using var f = await StoreFixture.CreateAsync();
        var one = await IssueAsync(f); var two = await IssueAsync(f);
        await Policy(f).RecordSourceRequestResultAsync(new(one.Token, SourceRequestOutcome.RateLimited,
            f.Clock.GetUtcNow(), receivedAtUtc: f.Clock.GetUtcNow()), default);
        await Policy(f).RecordSourceRequestResultAsync(new(two.Token, SourceRequestOutcome.AccessRestricted, f.Clock.GetUtcNow()), default);
        var paused = await Policy(f).ReadSourcePolicyAsync(Source, default);
        await using var other = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(f.DatabasePath), f.Clock);
        var command = new SourceAccessResumeCommand(Source, paused.Revision);
        var results = await Task.WhenAll(Policy(f).ResumeSourceAccessAsync(command, default),
            ((ISourcePolicyStore)other).ResumeSourceAccessAsync(command, default));
        Assert.Single(results, result => result.Applied);
        Assert.Single(results, result => !result.Applied);
        var resumed = await Policy(f).ReadSourcePolicyAsync(Source, default);
        Assert.Equal(paused.Revision + 1, resumed.Revision);
        Assert.Null(resumed.AccessPause);
        Assert.Equal(paused.Cooldown, resumed.Cooldown);
    }

    [Theory]
    [InlineData("retry_kind")]
    [InlineData("transport_start")]
    [InlineData("diagnostic")]
    public async Task CanonicalUtcReplayPreservesEvidenceButChangedTransportFactsConflict(string changed)
    {
        await using var f = await StoreFixture.CreateAsync();
        var issued = await IssueAsync(f); var now = f.Clock.GetUtcNow();
        var received = now.AddSeconds(1); var completed = now.AddSeconds(2);
        f.Clock.Set(completed);
        var original = new SourceRequestResultCommand(issued.Token, SourceRequestOutcome.RateLimited, completed,
            now, received, SourceRetryAfter.Delta(TimeSpan.FromMinutes(2)), "source_rate_limited");
        var receipt = await Policy(f).RecordSourceRequestResultAsync(original, default);
        await f.ReopenAsync();
        Assert.Equal(receipt, await Policy(f).RecordSourceRequestResultAsync(new(issued.Token, original.Outcome,
            completed.ToOffset(TimeSpan.FromHours(9)), now.ToOffset(TimeSpan.FromHours(9)), received.ToOffset(TimeSpan.FromHours(9)),
            SourceRetryAfter.Delta(TimeSpan.FromMinutes(2)), original.DiagnosticCode), default));
        var conflicting = new SourceRequestResultCommand(issued.Token, original.Outcome, completed,
            changed == "transport_start" ? now.AddTicks(1) : now, received,
            changed == "retry_kind" ? SourceRetryAfter.ResolvedDelay(TimeSpan.FromMinutes(2)) : original.RetryAfter,
            changed == "diagnostic" ? "changed_code" : original.DiagnosticCode);
        await Assert.ThrowsAsync<SourceRequestReplayConflictException>(() => Policy(f).RecordSourceRequestResultAsync(conflicting, default));
        Assert.Equal(receipt.PolicyRevision, (await Policy(f).ReadSourcePolicyAsync(Source, default)).Revision);
    }

    private static async Task<IssuedSourceRequest> IssueAsync(StoreFixture f) =>
        Assert.IsType<IssuedSourceRequest>((await Policy(f).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.SearchPage), default)).Issued);

    private sealed class EdgeFault() : Exception("Injected independent source-policy failure.");
}
