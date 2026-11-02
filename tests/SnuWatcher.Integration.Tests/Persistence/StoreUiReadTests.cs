using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreUiReadTests
{
    private static IWatchDashboardReader Reads(StoreFixture fixture) => (IWatchDashboardReader)(object)fixture.Store;

    [Fact]
    public async Task PagesUseCanonicalIdOrderAndExcludeRemovedDefinitionsWithoutChangingStoredState()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var keys = new[] { StoreFixture.Key("001"), StoreFixture.Key("002"), StoreFixture.Key("003"), StoreFixture.Key("004") };
        var watches = new List<SubscriptionSnapshot>();
        foreach (var index in new[] { 3, 1, 4, 2 })
            watches.Add(await fixture.Store.CreateSubscriptionAsync(new(Id(index), "강의 '); -- " + index,
                SubscriptionKind.Section, [keys[index - 1]], false, null, fixture.Clock.GetUtcNow())));
        var removed = watches.Single(x => x.Id == Id(2));
        await fixture.Store.ChangeSubscriptionAsync(new RemoveSubscription(removed.Id, removed.DefinitionRevision, fixture.Clock.GetUtcNow()));

        var first = await Reads(fixture).ReadWatchPageAsync(pageSize: 2);
        Assert.Equal(WatchPageReadDisposition.Page, first.Disposition);
        Assert.Equal(new[] { Id(1), Id(3) }, first.Page!.Items.Select(x => x.Id));
        Assert.True(first.Page.HasMore);
        Assert.Equal(Id(3), first.Page.NextCursor!.AfterSubscriptionId);
        Assert.All(first.Page.Items, x => Assert.Equal(0, x.SingleSection!.Version));
        var second = await Reads(fixture).ReadWatchPageAsync(first.Page.NextCursor, 2);
        Assert.Equal(Id(4), Assert.Single(second.Page!.Items).Id);
        Assert.False(second.Page.HasMore);
        Assert.Null(second.Page.NextCursor);
        Assert.Null(await Reads(fixture).ReadWatchDetailsAsync(Id(2)));
        Assert.Null(await Reads(fixture).ReadWatchDetailsAsync(Id(99)));
        Assert.Equal(4L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM Subscriptions;"));
    }

    [Fact]
    public async Task DefinitionChangeRestartsCursorButSuccessfulPollingKeepsEpochAndFreshSingleSectionFacts()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var firstWatch = await fixture.Store.CreateSubscriptionAsync(new(Id(1), "One", SubscriptionKind.Section,
            [StoreFixture.Key("001")], false, null, fixture.Clock.GetUtcNow()));
        await fixture.Store.CreateSubscriptionAsync(new(Id(2), "Two", SubscriptionKind.Section,
            [StoreFixture.Key("002")], false, null, fixture.Clock.GetUtcNow()));
        var first = (await Reads(fixture).ReadWatchPageAsync(pageSize: 1)).Page!;
        await fixture.ObserveAsync(StoreFixture.Key("002"), 116);
        var next = await Reads(fixture).ReadWatchPageAsync(first.NextCursor, 1);
        Assert.Equal(WatchPageReadDisposition.Page, next.Disposition);
        Assert.Equal(116, Assert.Single(next.Page!.Items).SingleSection!.Baseline!.Seats.Registered);
        Assert.Equal(first.Stamp.CatalogRevision, next.Page.Stamp.CatalogRevision);
        Assert.True(next.Page.Stamp.IssuedCheckOrdinal > first.Stamp.IssuedCheckOrdinal);
        await fixture.Store.ChangeSubscriptionAsync(new RenameSubscription(firstWatch.Id, firstWatch.DefinitionRevision, fixture.Clock.GetUtcNow(), "Renamed"));
        var restart = await Reads(fixture).ReadWatchPageAsync(first.NextCursor, 1);
        Assert.Equal(WatchPageReadDisposition.RestartRequired, restart.Disposition);
        Assert.Null(restart.Page);
        Assert.True(restart.CurrentCatalogRevision > first.Stamp.CatalogRevision);
    }

    [Fact]
    public async Task CollapsedLargeGroupIsASummaryAndDetailsReturnEveryCurrentMemberIncludingUnchecked()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var keys = Enumerable.Range(0, 2000).Select(index => new SectionKey(2026, "U000200002", "U000300002", "F32.102",
            (index % 1000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
            (index / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var watch = await fixture.Store.CreateSubscriptionAsync(new(Id(1), "Any section", SubscriptionKind.Group, keys, true, null, fixture.Clock.GetUtcNow()));
        await fixture.ObserveAsync(keys[0], 119);
        var paused = await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow()));
        var resumed = await fixture.Store.ChangeSubscriptionAsync(new ResumeSubscription(watch.Id, paused.DefinitionRevision, fixture.Clock.GetUtcNow()));
        var summary = Assert.Single((await Reads(fixture).ReadWatchPageAsync()).Page!.Items);
        Assert.Equal(2000, summary.MemberCount);
        Assert.Null(summary.SingleSection);
        Assert.Equal(2, summary.ActivationGeneration);
        Assert.Equal("U000300002", summary.Term.DetailSemesterCode);
        var details = Assert.IsType<WatchDetailsSnapshot>(await Reads(fixture).ReadWatchDetailsAsync(watch.Id));
        Assert.Equal(2000, details.Sections.Length);
        Assert.Equal(2000, details.Subscription.Members.Length);
        Assert.All(details.Subscription.Members, x => Assert.Null(x.InitializedByAttemptOrdinal));
        Assert.Equal(resumed.DefinitionRevision, details.Subscription.DefinitionRevision);
        Assert.Equal(119, details.Sections.Single(x => x.Key == keys[0]).Baseline!.Seats.Registered);
        Assert.Equal(1999, details.Sections.Count(x => x.Version == 0));
        Assert.True(keys.ToHashSet().SetEquals(details.Sections.Select(x => x.Key)));
    }

    [Fact]
    public async Task SourceStatusSeparatesPhysicalRequestEvidenceFromLogicalCheckOrderAndPreservesClockChanges()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var initial = await Reads(fixture).ReadSourceStatusAsync();
        Assert.Equal(0, initial.Policy.Progress.LastIssuedOrdinal);
        Assert.Null(initial.LatestCompletedTransport);
        var key = StoreFixture.Key();
        await fixture.IssueAsync(key);
        await fixture.IssueAsync(key);
        var source = (ISourcePolicyStore)fixture.Store;
        var request = (await source.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Detail), default)).Issued!;
        var completedAt = fixture.Clock.GetUtcNow().AddMinutes(-5);
        var startedAt = fixture.Clock.GetUtcNow().AddMinutes(1);
        await source.RecordSourceRequestResultAsync(new(request.Token, SourceRequestOutcome.Success, completedAt,
            actualStartedAtUtc: startedAt, receivedAtUtc: completedAt), default);
        var read = await Reads(fixture).ReadSourceStatusAsync();
        Assert.Equal(2, read.Stamp.IssuedCheckOrdinal);
        Assert.Equal(1, read.LatestCompletedTransport!.RequestOrdinal);
        Assert.Equal(startedAt, read.LatestCompletedTransport.ActualStartedAtUtc);
        Assert.Equal(completedAt, read.LatestCompletedTransport.ReceivedAtUtc);
        Assert.Equal(completedAt, read.LatestCompletedTransport.CompletedAtUtc);
        Assert.Equal(fixture.Clock.GetUtcNow(), read.Stamp.ReadAtUtc);
    }

    [Fact]
    public async Task NonHttpSourceCompletionKeepsActualTransportTimesUnknownAndReadsNeverWrite()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var source = (ISourcePolicyStore)fixture.Store;
        var request = (await source.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Catalog), default)).Issued!;
        await source.RecordSourceRequestResultAsync(new(request.Token, SourceRequestOutcome.NetworkFailure, fixture.Clock.GetUtcNow()), default);
        var before = StoreUiPreferencesTests.Scalar(fixture, "SELECT snapshot FROM SourcePolicies;");
        var snapshot = await Reads(fixture).ReadSourceStatusAsync();
        Assert.Null(snapshot.LatestCompletedTransport!.ActualStartedAtUtc);
        Assert.Null(snapshot.LatestCompletedTransport.ReceivedAtUtc);
        Assert.Equal(before, StoreUiPreferencesTests.Scalar(fixture, "SELECT snapshot FROM SourcePolicies;"));
        Assert.Empty(await fixture.Store.ListSubscriptionsAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task UnboundedPageRequestsFailAtThePort(int size)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reads(fixture).ReadWatchPageAsync(pageSize: size));
    }

    private static Guid Id(int suffix) => Guid.ParseExact(suffix.ToString("x32", System.Globalization.CultureInfo.InvariantCulture), "N");
}
