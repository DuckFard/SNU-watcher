using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreSourcePolicyTests
{
    private const string Source = SourcePolicyIds.SnuSugang;
    private static ISourcePolicyStore Policy(StoreFixture fixture) => (ISourcePolicyStore)(object)fixture.Store;

    [Fact]
    public async Task RequestIntentAndRestrictionPersistWithoutSectionSideEffects()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = Policy(fixture);
        var initial = await store.ReadSourcePolicyAsync(Source, default);
        Assert.Equal(0, initial.Progress.LastIssuedOrdinal);
        var issued = Assert.IsType<IssuedSourceRequest>((await store.TryIssueSourceRequestAsync(
            new(Source, SourceRequestKind.SearchPage), default)).Issued);
        Assert.Equal(1, issued.Token.Ordinal);
        var receipt = await store.RecordSourceRequestResultAsync(new(issued.Token, SourceRequestOutcome.RateLimited,
            fixture.Clock.GetUtcNow(), receivedAtUtc: fixture.Clock.GetUtcNow(),
            retryAfter: SourceRetryAfter.HttpDate(fixture.Clock.GetUtcNow().AddHours(2))), default);
        Assert.True(receipt.PolicyApplied);
        await fixture.ReopenAsync();
        store = Policy(fixture);
        var policy = await store.ReadSourcePolicyAsync(Source, default);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(2), policy.Cooldown!.NotBeforeUtc);
        Assert.False((await store.TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default)).WasIssued);
        var section = await fixture.Store.ReadSectionAsync(StoreFixture.Key(), default);
        Assert.Equal(0, section.Version);
        Assert.Null(section.Baseline);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Empty(await fixture.Store.ListSubscriptionsAsync(default));
    }

    [Fact]
    public async Task ExactResultReplayPrecedesOrderButChangedFactsConflictAfterReopen()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = (await Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Catalog), default)).Issued!;
        var second = (await Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default)).Issued!;
        var command = new SourceRequestResultCommand(second.Token, SourceRequestOutcome.Success, fixture.Clock.GetUtcNow());
        var receipt = await Policy(fixture).RecordSourceRequestResultAsync(command, default);
        var older = await Policy(fixture).RecordSourceRequestResultAsync(new(first.Token,
            SourceRequestOutcome.AccessRestricted, fixture.Clock.GetUtcNow()), default);
        Assert.False(older.PolicyApplied);
        await fixture.ReopenAsync();
        Assert.Equal(receipt, await Policy(fixture).RecordSourceRequestResultAsync(command, default));
        await Assert.ThrowsAsync<SourceRequestReplayConflictException>(() => Policy(fixture).RecordSourceRequestResultAsync(
            new(second.Token, SourceRequestOutcome.Unavailable, fixture.Clock.GetUtcNow()), default));
        var policy = await Policy(fixture).ReadSourcePolicyAsync(Source, default);
        Assert.Null(policy.AccessPause);
        Assert.Equal(2, policy.Progress.LastSuccessfulOrdinal);
    }

    [Fact]
    public async Task ExplicitResumeUsesRevisionAndIssuedBarrierWhileRetainingCooldown()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var one = (await Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.SearchPage), default)).Issued!;
        var two = (await Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default)).Issued!;
        var three = (await Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Catalog), default)).Issued!;
        await Policy(fixture).RecordSourceRequestResultAsync(new(one.Token, SourceRequestOutcome.RateLimited,
            fixture.Clock.GetUtcNow(), receivedAtUtc: fixture.Clock.GetUtcNow()), default);
        await Policy(fixture).RecordSourceRequestResultAsync(new(two.Token, SourceRequestOutcome.AccessRestricted,
            fixture.Clock.GetUtcNow()), default);
        var paused = await Policy(fixture).ReadSourcePolicyAsync(Source, default);
        Assert.NotNull(paused.AccessPause);
        Assert.False((await Policy(fixture).ResumeSourceAccessAsync(new(Source, paused.Revision - 1), default)).Applied);
        var resumed = await Policy(fixture).ResumeSourceAccessAsync(new(Source, paused.Revision), default);
        Assert.True(resumed.Applied);
        Assert.Null(resumed.Policy.AccessPause);
        Assert.Equal(3, resumed.Policy.ResumeWatermark);
        Assert.Equal(paused.Cooldown, resumed.Policy.Cooldown);
        await Policy(fixture).RecordSourceRequestResultAsync(new(three.Token, SourceRequestOutcome.AccessRestricted,
            fixture.Clock.GetUtcNow()), default);
        Assert.Null((await Policy(fixture).ReadSourcePolicyAsync(Source, default)).AccessPause);
        Assert.False((await Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default)).WasIssued);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(4, (await Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default)).Issued!.Token.Ordinal);
    }

    [Fact]
    public async Task UnissuedOrWrongOrdinalResultsFailWithoutPersistingHealth()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var issued = (await Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default)).Issued!;
        await Assert.ThrowsAsync<ArgumentException>(() => Policy(fixture).RecordSourceRequestResultAsync(new(
            new SourceRequestToken(issued.Token.Id, Source, issued.Token.Ordinal + 1), SourceRequestOutcome.Success,
            fixture.Clock.GetUtcNow()), default));
        await Assert.ThrowsAsync<ArgumentException>(() => Policy(fixture).RecordSourceRequestResultAsync(new(
            new SourceRequestToken(Guid.NewGuid(), Source, 1), SourceRequestOutcome.Success, fixture.Clock.GetUtcNow()), default));
        Assert.Equal(0, (await Policy(fixture).ReadSourcePolicyAsync(Source, default)).Progress.LatestCompletedOrdinal);
    }

    [Fact]
    public async Task ConcurrentIssuanceHasDistinctDurableOrdinals()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await using var other = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(fixture.DatabasePath), fixture.Clock);
        var results = await Task.WhenAll(Policy(fixture).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Catalog), default),
            ((ISourcePolicyStore)(object)other).TryIssueSourceRequestAsync(new(Source, SourceRequestKind.Detail), default));
        Assert.Equal(new long[] { 1, 2 }, results.Select(result => result.Issued!.Token.Ordinal).Order());
        await fixture.ReopenAsync();
        Assert.Equal(2, (await Policy(fixture).ReadSourcePolicyAsync(Source, default)).Progress.LastIssuedOrdinal);
    }
}
