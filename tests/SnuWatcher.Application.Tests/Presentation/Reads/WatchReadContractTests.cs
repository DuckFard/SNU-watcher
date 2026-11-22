using System.Collections.Immutable;
using System.Reflection;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Presentation.Reads;

public sealed class WatchReadContractTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T01:00:00Z");
    private static readonly Guid First = Guid.ParseExact("00000000000000000000000000000001", "N");
    private static readonly Guid Second = Guid.ParseExact("00000000000000000000000000000002", "N");

    [Fact]
    public void ReadStampIsNotAnObservationAndTermIdentityPreservesTheValidatedFullTerm()
    {
        var stamp = new UiReadStamp(0, 12, Now.ToOffset(TimeSpan.FromHours(9)));
        var term = new AcademicTermIdentity(Key());
        Assert.Equal(0, stamp.CatalogRevision);
        Assert.Equal(12, stamp.IssuedCheckOrdinal);
        Assert.Equal(Now, stamp.ReadAtUtc);
        Assert.Equal(TimeSpan.Zero, stamp.ReadAtUtc.Offset);
        Assert.Equal(2026, term.Year);
        Assert.Equal("U000200002", term.SemesterCode);
        Assert.Equal("U000300002", term.DetailSemesterCode);
        Assert.Equal(term, new AcademicTermIdentity(Key("002")));
        Assert.NotEqual(term, new AcademicTermIdentity(Key(detail: "U000300001")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiReadStamp(-1, 0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiReadStamp(0, -1, Now));
        Assert.Throws<ArgumentNullException>(() => new AcademicTermIdentity(null!));
    }

    [Fact]
    public void ASectionSummaryHasOneExactSectionAndAGroupsStateRemainsLazy()
    {
        var single = Summary(First);
        Assert.Equal(First, single.Id);
        Assert.Equal(1, single.DefinitionRevision);
        Assert.Equal(2, single.ActivationGeneration);
        Assert.Equal(SubscriptionKind.Section, single.Kind);
        Assert.Equal("통계학 \"012\"", single.Label);
        Assert.Equal(WatchStatus.Paused, single.StoredStatus);
        Assert.True(single.InitialNoticeEnabled);
        Assert.Null(single.Expiry);
        Assert.Equal(Now, single.CreatedAtUtc);
        Assert.Equal("012", single.SingleSection!.Key.Section);
        Assert.Equal(new AcademicTermIdentity(Key()), single.Term);
        Assert.Equal(1, single.MemberCount);
        var group = Group(2000);
        Assert.Equal(SubscriptionKind.Group, group.Kind);
        Assert.Equal(2000, group.MemberCount);
        Assert.Null(group.SingleSection);
        Assert.Throws<ArgumentException>(() => Summary(First, state: SectionSnapshot.Unchecked(Key(detail: "U000300001"))));
        Assert.Throws<ArgumentException>(() => Group(1, SectionSnapshot.Unchecked(Key())));
        Assert.Throws<ArgumentException>(() => Summary(First, memberCount: 2));
        Assert.Throws<ArgumentException>(() => Summary(First, status: WatchStatus.Removed));
        Assert.Throws<ArgumentException>(() => Summary(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => Group(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Group(2001));
    }

    [Fact]
    public void PagesSnapshotInputAndBindTheCursorToTheLastCanonicalIdAndCatalogEpoch()
    {
        var items = new List<WatchDefinitionSummary> { Summary(First), Summary(Second) };
        var stamp = new UiReadStamp(7, 14, Now);
        var cursor = new WatchPageCursor(7, Second);
        var page = new WatchPageSnapshot(stamp, items, true, cursor);
        items.Clear();
        Assert.Equal(2, page.Items.Length);
        Assert.Same(stamp, page.Stamp);
        Assert.True(page.HasMore);
        Assert.Same(cursor, page.NextCursor);
        Assert.Equal(7, cursor.CatalogRevision);
        Assert.Equal(Second, cursor.AfterSubscriptionId);
        var result = new WatchPageReadResult(WatchPageReadDisposition.Page, 7, page);
        Assert.Same(page, result.Page);
        Assert.Equal(7, result.CurrentCatalogRevision);
        Assert.Equal(WatchPageReadDisposition.Page, result.Disposition);
        var restart = new WatchPageReadResult(WatchPageReadDisposition.RestartRequired, 8, null);
        Assert.Null(restart.Page);
        Assert.Throws<ArgumentException>(() => new WatchPageReadResult(WatchPageReadDisposition.RestartRequired, 8, page));
        Assert.Throws<ArgumentException>(() => new WatchPageReadResult(WatchPageReadDisposition.Page, 8, page));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, page.Items, true, new(8, Second)));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, page.Items, true, new(7, First)));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, page.Items, true, null));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, page.Items, false, cursor));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, [], true, cursor));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, page.Items.Reverse(), false, null));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, [Summary(First), Summary(First)], false, null));
    }

    [Fact]
    public void PageBoundsDoNotPermitDefaultOrOversizedOrNullSnapshots()
    {
        var stamp = new UiReadStamp(0, 0, Now);
        var fifty = Enumerable.Range(1, 50).Select(index => Summary(Guid.ParseExact(index.ToString("x32"), "N"))).ToArray();
        Assert.Equal(50, new WatchPageSnapshot(stamp, fifty, false, null).Items.Length);
        Assert.Empty(new WatchPageSnapshot(stamp, [], false, null).Items);
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, fifty.Append(Summary(Guid.NewGuid())), false, null));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, default(ImmutableArray<WatchDefinitionSummary>), false, null));
        Assert.Throws<ArgumentException>(() => new WatchPageSnapshot(stamp, [null!], false, null));
        Assert.Throws<ArgumentNullException>(() => new WatchPageSnapshot(stamp, null!, false, null));
        Assert.Throws<ArgumentNullException>(() => new WatchPageSnapshot(null!, [], false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchPageCursor(-1, First));
        Assert.Throws<ArgumentException>(() => new WatchPageCursor(0, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchPageReadResult((WatchPageReadDisposition)99, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchPageReadResult(WatchPageReadDisposition.RestartRequired, -1, null));
        Assert.Throws<ArgumentException>(() => new WatchPageReadResult(WatchPageReadDisposition.Page, 0, null));
    }

    [Fact]
    public void DetailsRequireExactlyCurrentMemberStatesAndPreserveUncheckedInsteadOfInventingCounts()
    {
        var members = new[] { new MemberSnapshot(Key(), 0, null), new MemberSnapshot(Key("002"), 0, null) };
        var subscription = new SubscriptionSnapshot(First, 1, 1, SubscriptionKind.Group, "Any section", WatchStatus.Active, false, null, Now, members);
        var states = new List<SectionSnapshot> { SectionSnapshot.Unchecked(Key("002")), SectionSnapshot.Unchecked(Key()) };
        var stamp = new UiReadStamp(1, 0, Now);
        var details = new WatchDetailsSnapshot(stamp, subscription, states);
        states.Clear();
        Assert.Same(stamp, details.Stamp);
        Assert.Same(subscription, details.Subscription);
        Assert.Equal(2, details.Sections.Length);
        Assert.All(details.Sections, state => Assert.Null(state.Baseline));
        Assert.Throws<ArgumentException>(() => new WatchDetailsSnapshot(stamp, subscription, [SectionSnapshot.Unchecked(Key())]));
        Assert.Throws<ArgumentException>(() => new WatchDetailsSnapshot(stamp, subscription, [SectionSnapshot.Unchecked(Key()), SectionSnapshot.Unchecked(Key())]));
        Assert.Throws<ArgumentException>(() => new WatchDetailsSnapshot(stamp, subscription, [SectionSnapshot.Unchecked(Key()), SectionSnapshot.Unchecked(Key("003"))]));
        Assert.Throws<ArgumentNullException>(() => new WatchDetailsSnapshot(stamp, null!, []));
        Assert.Throws<ArgumentNullException>(() => new WatchDetailsSnapshot(null!, subscription, details.Sections));
    }

    [Fact]
    public void TransportEvidenceMatchesCompletedPhysicalIntentWithoutComparingUnrelatedOrdersOrUtcChronology()
    {
        var progress = new SourceRequestProgress(99, Now, 90, SourceRequestOutcome.Success, Now, 90, Now);
        var policy = new SourcePolicySnapshot(SourcePolicyIds.SnuSugang, 2, progress);
        var evidence = new CompletedTransportEvidence(90, Now.AddMinutes(10), Now.AddMinutes(5), Now.ToOffset(TimeSpan.FromHours(9)));
        var status = new SourceStatusSnapshot(new(1, 0, Now), policy, evidence);
        Assert.Equal(90, evidence.RequestOrdinal);
        Assert.Equal(Now.AddMinutes(10), evidence.ActualStartedAtUtc);
        Assert.Equal(Now.AddMinutes(5), evidence.ReceivedAtUtc);
        Assert.Equal(TimeSpan.Zero, evidence.CompletedAtUtc.Offset);
        Assert.Same(evidence, status.LatestCompletedTransport);
        Assert.Same(policy, status.Policy);
        Assert.Equal(0, status.Stamp.IssuedCheckOrdinal);
        Assert.Null(new SourceStatusSnapshot(new(0, 0, Now), policy, null).LatestCompletedTransport);
        Assert.Null(new CompletedTransportEvidence(1, null, null, Now).ActualStartedAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompletedTransportEvidence(0, null, null, Now));
        Assert.Throws<ArgumentException>(() => new SourceStatusSnapshot(new(0, 0, Now), policy, new(89, null, null, Now)));
        Assert.Throws<ArgumentException>(() => new SourceStatusSnapshot(new(0, 0, Now), policy, new(90, null, null, Now.AddSeconds(1))));
        Assert.Throws<ArgumentNullException>(() => new SourceStatusSnapshot(new(0, 0, Now), null!, null));
        Assert.Throws<ArgumentNullException>(() => new SourceStatusSnapshot(null!, policy, null));
    }

    [Fact]
    public void AllReadModelsExposeNoWritablePublicProperties()
    {
        Type[] types = [typeof(UiReadStamp), typeof(AcademicTermIdentity), typeof(WatchPageCursor), typeof(WatchDefinitionSummary),
            typeof(WatchPageSnapshot), typeof(WatchPageReadResult), typeof(WatchDetailsSnapshot), typeof(CompletedTransportEvidence), typeof(SourceStatusSnapshot)];
        Assert.All(types, type => Assert.All(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            property => Assert.Null(property.SetMethod)));
    }

    private static WatchDefinitionSummary Summary(Guid id, SectionSnapshot? state = null, int memberCount = 1, WatchStatus status = WatchStatus.Paused) =>
        new(id, 1, 2, SubscriptionKind.Section, " 통계학 \"012\" ", status, true, null, Now, new(Key()), memberCount, state ?? SectionSnapshot.Unchecked(Key()));
    private static WatchDefinitionSummary Group(int count, SectionSnapshot? state = null) =>
        new(First, 1, 1, SubscriptionKind.Group, "College Writing", WatchStatus.Active, false, null, Now, new(Key()), count, state);
    private static SectionKey Key(string section = "012", string detail = "U000300002") => new(2026, "U000200002", detail, "F32.102", section, "0");
}
