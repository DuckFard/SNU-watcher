using Xunit;

namespace SnuWatcher.Core.Tests;

public sealed class RecipientResolverTests
{
    private static readonly SectionKey Key = Fixture.Key();
    private static readonly DateTimeOffset Now = Fixture.At(10);

    private static (SeatObservation Obs, OpeningEvaluation Eval) Step(SeatBaseline? baseline, long seq, int reg, int cap)
    {
        var obs = Fixture.Valid(Key, seq, reg, cap);
        return (obs, OpeningPolicy.Evaluate(baseline, obs));
    }

    private static SeatBaseline Baseline(long seq, int reg, int cap) =>
        Step(null, seq, reg, cap).Eval.NextBaseline!;

    [Fact]
    public void InitializedMemberIsAddressedForAnIncrease()
    {
        var (obs, eval) = Step(Baseline(1, 120, 120), 2, 119, 120);
        var result = RecipientResolver.Resolve([Fixture.Member("a", Key, initialized: true)], obs, eval, Now);

        var n = result.Notification!;
        Assert.Equal(1, n.CurrentSeats);
        Assert.Equal(0, n.PreviousSeats);
        Assert.Equal(OpeningReason.IncreasedSeats, n.Recipients.Single().Reason);
        Assert.Empty(result.Initializations);
    }

    [Fact]
    public void NewMemberWithInitialNoticeOnGetsInitialAvailabilityOnAnOpenSection()
    {
        var (obs, eval) = Step(Baseline(1, 119, 120), 2, 119, 120);   // unchanged: no shared opening
        var result = RecipientResolver.Resolve([Fixture.Member("new", Key, activation: 1)], obs, eval, Now);

        Assert.Equal(OpeningReason.InitialAvailability, result.Notification!.Recipients.Single().Reason);
        Assert.Equal(new[] { new MemberInitialization("new", 1) }, result.Initializations);
    }

    [Fact]
    public void NewMemberWithInitialNoticeOffStaysSilentEvenWhenAnOlderWatchIsAddressed()
    {
        var (obs, eval) = Step(Baseline(1, 120, 120), 2, 118, 120);   // shared increase 0 -> 2
        var members = new[]
        {
            Fixture.Member("old", Key, initialized: true),
            Fixture.Member("fresh", Key, activation: 1, initial: false),
        };
        var result = RecipientResolver.Resolve(members, obs, eval, Now);

        Assert.Equal(new[] { "old" }, result.Notification!.Recipients.Select(r => r.SubscriptionId).ToArray());
        Assert.Equal(new[] { new MemberInitialization("fresh", 1) }, result.Initializations);
    }

    [Fact]
    public void NewMemberOnAFullSectionInitializesWithoutAnAlert()
    {
        var (obs, eval) = Step(Baseline(1, 120, 120), 2, 120, 120);
        var result = RecipientResolver.Resolve([Fixture.Member("new", Key, activation: 1)], obs, eval, Now);

        Assert.Null(result.Notification);
        Assert.Single(result.Initializations);
    }

    [Fact]
    public void AttemptIssuedBeforeActivationCannotInitializeAMember()
    {
        var (obs, eval) = Step(Baseline(1, 120, 120), 5, 100, 120);
        var result = RecipientResolver.Resolve([Fixture.Member("late", Key, activation: 5)], obs, eval, Now);

        Assert.Null(result.Notification);
        Assert.Empty(result.Initializations);
    }

    [Fact]
    public void OverlappingIndividualAndGroupWatchesShareOnePopup()
    {
        var (obs, eval) = Step(Baseline(1, 120, 120), 2, 119, 120);
        var members = new[]
        {
            Fixture.Member("individual", Key, initialized: true),
            Fixture.Member("group:college-writing", Key, initialized: true),
        };
        var n = RecipientResolver.Resolve(members, obs, eval, Now).Notification!;

        Assert.Equal(2, n.Recipients.Count);
    }

    [Fact]
    public void PausedExpiredAndOtherSectionMembersAreExcluded()
    {
        var (obs, eval) = Step(Baseline(1, 120, 120), 2, 119, 120);
        var members = new[]
        {
            Fixture.Member("paused", Key, initialized: true, status: SubscriptionStatus.Paused),
            Fixture.Member("expired-flag", Key, initialized: true, status: SubscriptionStatus.Expired),
            Fixture.Member("expired-time", Key, initialized: true, expires: Now),   // boundary is exclusive
            Fixture.Member("other-section", Fixture.Key(section: "001"), initialized: true),
            Fixture.Member("live", Key, initialized: true, expires: Now.AddSeconds(1)),
        };
        var n = RecipientResolver.Resolve(members, obs, eval, Now).Notification!;

        Assert.Equal(new[] { "live" }, n.Recipients.Select(r => r.SubscriptionId).ToArray());
    }

    [Fact]
    public void RejectedEvaluationsAddressNobodyAndInitializeNobody()
    {
        var baseline = Baseline(5, 120, 120);
        var members = new[] { Fixture.Member("a", Key, initialized: true), Fixture.Member("b", Key, activation: 1) };

        var stale = Step(baseline, 5, 100, 120);
        var unknown = (Obs: SeatObservation.Unknown(Key, 6, Fixture.At(6)), Eval: OpeningPolicy.Evaluate(baseline, SeatObservation.Unknown(Key, 6, Fixture.At(6))));

        Assert.Same(RecipientResolution.None, RecipientResolver.Resolve(members, stale.Obs, stale.Eval, Now));
        Assert.Same(RecipientResolution.None, RecipientResolver.Resolve(members, unknown.Obs, unknown.Eval, Now));
    }

    [Fact]
    public void InitializedMemberIsSilentWithoutAnIncrease()
    {
        var (obs, eval) = Step(Baseline(1, 119, 120), 2, 119, 120);
        var result = RecipientResolver.Resolve([Fixture.Member("a", Key, initialized: true)], obs, eval, Now);

        Assert.Null(result.Notification);
    }

    [Fact]
    public void ResumeGenerationInitializesIndependentlyOfTheSharedBaseline()
    {
        // Same subscription resumed as generation 2; an old-generation record must not suppress it.
        var (obs, eval) = Step(Baseline(1, 119, 120), 9, 119, 120);
        var resumed = Fixture.Member("a", Key, activation: 8, generation: 2);
        var result = RecipientResolver.Resolve([resumed], obs, eval, Now);

        Assert.Equal(2, result.Notification!.Recipients.Single().Generation);
        Assert.Equal(new[] { new MemberInitialization("a", 2) }, result.Initializations);
    }
}
