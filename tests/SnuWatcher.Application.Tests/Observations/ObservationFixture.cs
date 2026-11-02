using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Tests.Observations;

internal static class ObservationFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    internal static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static SectionKey Key(string section = "012") => new(2026, "2", "U000200002", "F32.102", section, "000");

    internal static SectionCompletion Valid(long ordinal, int registered = 119, int capacity = 120,
        SectionKey? key = null, DateTimeOffset? observedAt = null, QuotaMeaning? meaning = null)
    {
        var token = new CheckAttemptToken(Guid.NewGuid(), ordinal);
        return new(token, SeatObservation.Valid(key ?? Key(), ordinal, observedAt ?? Now.AddMinutes(ordinal),
            new SeatCount(registered, capacity), meaning ?? QuotaMeaning.EnrolledOverQuota), CheckOutcome.Success);
    }

    internal static SectionCompletion Unknown(long ordinal, CheckOutcome outcome = CheckOutcome.Unavailable, SectionKey? key = null)
    {
        var token = new CheckAttemptToken(Guid.NewGuid(), ordinal);
        return new(token, SeatObservation.Unknown(key ?? Key(), ordinal, Now.AddMinutes(ordinal)), outcome, "source.unavailable");
    }

    internal static SubscriptionSnapshot Watch(Guid? id = null, SectionKey? key = null, bool initial = true,
        bool initialized = false, long watermark = 0, long generation = 1, WatchStatus status = WatchStatus.Active,
        WatchExpiry? expiry = null, IEnumerable<MemberSnapshot>? members = null)
    {
        var membership = members?.ToImmutableArray()
            ?? [new MemberSnapshot(key ?? Key(), watermark, initialized ? watermark + 1 : null)];
        return new(id ?? FirstId, 1, generation, membership.Length > 1 ? SubscriptionKind.Group : SubscriptionKind.Section,
            "Statistics", status, initial, expiry, Now, membership);
    }

    internal static CommitContext Context(SectionCompletion completion, SectionSnapshot? section = null,
        IEnumerable<SubscriptionSnapshot>? watches = null, long catalogRevision = 7)
    {
        section ??= SectionSnapshot.Unchecked(completion.Key);
        return new(new IssuedCheck(completion.Token, completion.ObservedAtUtc.AddSeconds(-1), [section.Key]),
            section, catalogRevision, watches ?? []);
    }

    internal static SectionSnapshot State(long ordinal = 1, int registered = 120, int capacity = 120)
    {
        var baseline = new SeatBaseline(Key(), new SeatCount(registered, capacity), QuotaMeaning.EnrolledOverQuota,
            ordinal, Now.AddMinutes(ordinal));
        return new(Key(), ordinal, ordinal, CheckOutcome.Success, baseline.ObservedAt, baseline.ObservedAt, baseline);
    }

    internal static (SectionSnapshot Section, ImmutableArray<SubscriptionSnapshot> Watches) Apply(
        SectionSnapshot section, IEnumerable<SubscriptionSnapshot> watches, PreparedSectionCommit commit)
    {
        if (commit.Effects.EvaluationOutcome == EvaluationOutcome.IgnoredStale) return (section, watches.ToImmutableArray());
        var baseline = commit.Effects.NextBaseline ?? section.Baseline;
        var next = new SectionSnapshot(section.Key, section.Version + 1, commit.Completion.Token.Ordinal,
            commit.Effects.HealthOutcome, commit.Completion.ObservedAtUtc, baseline?.ObservedAt, baseline);
        var subscriptions = watches.Select(s => new SubscriptionSnapshot(s.Id, s.DefinitionRevision, s.ActivationGeneration,
            s.Kind, s.Label, s.Status, s.InitialNoticeEnabled, s.Expiry, s.CreatedAtUtc,
            s.Members.Select(m => m.Key == commit.Completion.Key && commit.Effects.Initializations.Any(i =>
                i.SubscriptionId == s.Id && i.Generation == s.ActivationGeneration)
                ? new MemberSnapshot(m.Key, m.ActivationWatermark, commit.Completion.Token.Ordinal) : m))).ToImmutableArray();
        return (next, subscriptions);
    }
}
