namespace SnuWatcher.Core;

public enum SubscriptionStatus { Active, Paused, Expired }

/// <summary>
/// One watched section as seen by one subscription (an individual watch or a group member).
/// <see cref="ActivationSequence"/> is the newest issued attempt at the moment the member was
/// created or resumed; only a later attempt may initialize this <see cref="Generation"/>.
/// </summary>
public sealed record WatchMember(string SubscriptionId, SectionKey Key, long Generation,
    long ActivationSequence, bool InitialNoticeEnabled, bool Initialized,
    SubscriptionStatus Status, DateTimeOffset? ExpiresAtUtc)
{
    public bool IsEligibleAt(DateTimeOffset now) =>
        Status == SubscriptionStatus.Active && (ExpiresAtUtc is null || now < ExpiresAtUtc);
}

/// <summary>Why one subscription is addressed by an event; retained for its history.</summary>
public sealed record Recipient(string SubscriptionId, long Generation, OpeningReason Reason);

/// <summary>Marks one member generation initialized by an accepted observation.</summary>
public sealed record MemberInitialization(string SubscriptionId, long Generation);

/// <summary>One popup for one section, however many overlapping subscriptions it addresses.</summary>
public sealed record OpeningNotification(SectionKey Key, int CurrentSeats, int? PreviousSeats,
    long AttemptSequence, DateTimeOffset ObservedAt, IReadOnlyList<Recipient> Recipients)
{
    private readonly IReadOnlyList<Recipient> _recipients = Array.AsReadOnly(Recipients.ToArray());

    /// <summary>A snapshot; callers cannot mutate recipient state through an input list or a returned collection.</summary>
    public IReadOnlyList<Recipient> Recipients
    {
        get => _recipients;
        init => _recipients = Array.AsReadOnly(value.ToArray());
    }
}

public sealed record RecipientResolution(OpeningNotification? Notification,
    IReadOnlyList<MemberInitialization> Initializations)
{
    private readonly IReadOnlyList<MemberInitialization> _initializations = Array.AsReadOnly(Initializations.ToArray());

    /// <summary>A snapshot, including when replaced with a record with-expression.</summary>
    public IReadOnlyList<MemberInitialization> Initializations
    {
        get => _initializations;
        init => _initializations = Array.AsReadOnly(value.ToArray());
    }

    public static RecipientResolution None { get; } = new(null, []);
}

/// <summary>
/// Addresses an accepted section evaluation to subscriptions. Shared baselines and per-member
/// initialization stay separate, so a new or resumed watch never resets anyone else's baseline,
/// and a new watch with initial notices off stays silent on its first observation even when that
/// observation is an increase for an older watch.
/// </summary>
public static class RecipientResolver
{
    public static RecipientResolution Resolve(IEnumerable<WatchMember> members, SeatObservation observation,
        OpeningEvaluation evaluation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!evaluation.Accepted || evaluation.CurrentSeats is not { } currentSeats) return RecipientResolution.None;

        var recipients = new List<Recipient>();
        var initializations = new List<MemberInitialization>();
        foreach (var member in members.Where(m => m.Key == observation.Key && m.IsEligibleAt(now)))
        {
            if (!member.Initialized)
            {
                // An attempt issued before this generation existed cannot initialize it.
                if (observation.AttemptSequence <= member.ActivationSequence) continue;
                initializations.Add(new(member.SubscriptionId, member.Generation));
                if (member.InitialNoticeEnabled && currentSeats > 0)
                    recipients.Add(new(member.SubscriptionId, member.Generation, OpeningReason.InitialAvailability));
            }
            else if (evaluation.Opening is { } opening)
            {
                recipients.Add(new(member.SubscriptionId, member.Generation, opening.Reason));
            }
        }

        var notification = recipients.Count == 0
            ? null
            : new OpeningNotification(observation.Key, currentSeats, evaluation.PreviousSeats,
                observation.AttemptSequence, observation.ObservedAt, recipients);
        return new RecipientResolution(notification, initializations);
    }
}
