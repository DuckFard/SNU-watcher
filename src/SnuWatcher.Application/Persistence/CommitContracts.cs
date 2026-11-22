using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

/// <summary>A consistent read of section state and every relevant subscription, including inactive ones.</summary>
public sealed record CommitContext
{
    public CommitContext(IssuedCheck issuedCheck, SectionSnapshot section, long catalogRevision,
        IEnumerable<SubscriptionSnapshot> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(issuedCheck);
        ArgumentNullException.ThrowIfNull(section);
        if (!issuedCheck.Scope.Contains(section.Key)) throw new ArgumentException("The section is outside the issued scope.", nameof(section));
        IssuedCheck = issuedCheck;
        Section = section;
        CatalogRevision = PersistenceValidation.Nonnegative(catalogRevision, nameof(catalogRevision));
        Subscriptions = PersistenceValidation.Snapshot(subscriptions, PersistenceLimits.MaxSections, nameof(subscriptions));
        if (Subscriptions.Select(s => s.Id).Distinct().Count() != Subscriptions.Length
            || Subscriptions.Any(s => !s.Members.Any(m => m.Key == section.Key)))
            throw new ArgumentException("Context subscriptions must be unique and contain the section.", nameof(subscriptions));
    }

    public IssuedCheck IssuedCheck { get; }
    public SectionSnapshot Section { get; }
    public long CatalogRevision { get; }
    public ImmutableArray<SubscriptionSnapshot> Subscriptions { get; }
}

public sealed record MemberInitializationEffect
{
    public MemberInitializationEffect(Guid subscriptionId, long generation)
    {
        SubscriptionId = PersistenceValidation.Id(subscriptionId, nameof(subscriptionId));
        Generation = PersistenceValidation.Positive(generation, nameof(generation));
    }

    public Guid SubscriptionId { get; }
    public long Generation { get; }
}

/// <summary>Pure prepared changes. Store persists health, baseline, members, event and outbox atomically.</summary>
public sealed record SectionEffects
{
    public SectionEffects(EvaluationOutcome evaluationOutcome, CheckOutcome healthOutcome, SeatBaseline? nextBaseline,
        IEnumerable<MemberInitializationEffect> initializations, OpeningNotification? notification)
    {
        EvaluationOutcome = PersistenceValidation.Defined(evaluationOutcome, nameof(evaluationOutcome));
        HealthOutcome = PersistenceValidation.Defined(healthOutcome, nameof(healthOutcome));
        var accepted = evaluationOutcome is EvaluationOutcome.BaselineEstablished or EvaluationOutcome.Opening or EvaluationOutcome.NoOpening;
        if (accepted != (nextBaseline is not null)) throw new ArgumentException("Only accepted valid effects replace a baseline.", nameof(nextBaseline));
        if (evaluationOutcome != EvaluationOutcome.IgnoredStale && accepted != (healthOutcome == CheckOutcome.Success))
            throw new ArgumentException("Only accepted valid effects have successful health.", nameof(healthOutcome));
        Initializations = PersistenceValidation.Snapshot(initializations, PersistenceLimits.MaxSections, nameof(initializations));
        if (Initializations.Distinct().Count() != Initializations.Length)
            throw new ArgumentException("Member initialization effects must be unique.", nameof(initializations));
        if (!accepted && (!Initializations.IsEmpty || notification is not null))
            throw new ArgumentException("Rejected or unknown effects cannot initialize members or create a notification.");
        if (notification is not null) PersistenceValidation.Notification(notification);
        NextBaseline = nextBaseline;
        Notification = notification;
    }

    public EvaluationOutcome EvaluationOutcome { get; }
    public CheckOutcome HealthOutcome { get; }
    public SeatBaseline? NextBaseline { get; }
    public ImmutableArray<MemberInitializationEffect> Initializations { get; }
    public OpeningNotification? Notification { get; }
}

public sealed record PreparedSectionCommit
{
    public PreparedSectionCommit(SectionCompletion completion, long expectedSectionVersion, long expectedCatalogRevision,
        SectionEffects effects, DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(effects);
        ValidateSourceEffects(completion, effects);
        if (effects.NextBaseline is { } baseline && (baseline.Key != completion.Key || baseline.AttemptSequence != completion.Token.Ordinal
            || baseline.ObservedAt != completion.Observation.ObservedAt || baseline.Seats != completion.Observation.Seats
            || baseline.Meaning != completion.Observation.Meaning))
            throw new ArgumentException("The next baseline must identify this completion.", nameof(effects));
        if (effects.Notification is { } notification && (notification.Key != completion.Key || notification.AttemptSequence != completion.Token.Ordinal
            || notification.ObservedAt != completion.Observation.ObservedAt || notification.CurrentSeats != effects.NextBaseline!.Seats.ApparentSeats))
            throw new ArgumentException("The notification must identify this completion and its current counts.", nameof(effects));
        Completion = completion;
        ExpectedSectionVersion = PersistenceValidation.Nonnegative(expectedSectionVersion, nameof(expectedSectionVersion));
        ExpectedCatalogRevision = PersistenceValidation.Nonnegative(expectedCatalogRevision, nameof(expectedCatalogRevision));
        Effects = effects;
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
    }

    public SectionCompletion Completion { get; }
    public long ExpectedSectionVersion { get; }
    public long ExpectedCatalogRevision { get; }
    public SectionEffects Effects { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }

    private static void ValidateSourceEffects(SectionCompletion completion, SectionEffects effects)
    {
        if (effects.EvaluationOutcome == EvaluationOutcome.IgnoredStale) return;
        var validKind = completion.Observation.Kind switch
        {
            ObservationKind.Unknown => effects.EvaluationOutcome == EvaluationOutcome.IgnoredUnknown,
            ObservationKind.SemanticsUnverified => effects.EvaluationOutcome == EvaluationOutcome.RevalidationRequired,
            ObservationKind.Valid => effects.EvaluationOutcome is EvaluationOutcome.BaselineEstablished
                or EvaluationOutcome.Opening or EvaluationOutcome.NoOpening or EvaluationOutcome.RevalidationRequired,
            _ => false,
        };
        var expectedHealth = effects.EvaluationOutcome == EvaluationOutcome.RevalidationRequired
            ? CheckOutcome.ParseChanged : completion.Outcome;
        if (!validKind || effects.HealthOutcome != expectedHealth)
            throw new ArgumentException("Prepared effects must preserve source facts and their effective health.", nameof(effects));
    }
}
