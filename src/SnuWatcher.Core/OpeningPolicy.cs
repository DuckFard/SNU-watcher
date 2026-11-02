namespace SnuWatcher.Core;

/// <summary>The last accepted valid observation for a section: the comparison baseline.</summary>
public sealed record SeatBaseline(SectionKey Key, SeatCount Seats, QuotaMeaning Meaning,
    long AttemptSequence, DateTimeOffset ObservedAt);

public enum OpeningReason
{
    /// <summary>The section was already open when first (or newly) observed.</summary>
    InitialAvailability,
    /// <summary>Apparent seats rose above the previous valid observation.</summary>
    IncreasedSeats,
}

/// <summary>A shared, section-level fact: apparent seats increased. Not yet addressed to anyone.</summary>
public sealed record SectionOpening(SectionKey Key, OpeningReason Reason, int? PreviousSeats,
    int CurrentSeats, long AttemptSequence, DateTimeOffset ObservedAt)
{
    public int Increase => CurrentSeats - (PreviousSeats ?? 0);
}

public enum EvaluationOutcome
{
    /// <summary>First valid observation and no seats: baseline stored, nothing to announce.</summary>
    BaselineEstablished,
    /// <summary>Apparent seats increased (or first sight was already open).</summary>
    Opening,
    /// <summary>Valid observation without an increase; the baseline still follows the new count.</summary>
    NoOpening,
    /// <summary>No usable counts. The last valid baseline is preserved.</summary>
    IgnoredUnknown,
    /// <summary>Quota meaning is unverified or changed. No baseline change, no event.</summary>
    RevalidationRequired,
    /// <summary>Replay or out-of-order response (attempt sequence not newer than the baseline).</summary>
    IgnoredStale,
}

public sealed record OpeningEvaluation(EvaluationOutcome Outcome, SeatBaseline? NextBaseline,
    SectionOpening? Opening, int? PreviousSeats, int? CurrentSeats)
{
    /// <summary>True when the observation was accepted as a valid count (baseline may have advanced).</summary>
    public bool Accepted => Outcome is EvaluationOutcome.BaselineEstablished
        or EvaluationOutcome.Opening or EvaluationOutcome.NoOpening;
}

/// <summary>
/// The pure section-level alert rule: alert whenever apparent free seats rise above the previous
/// valid observation, including while the section was already open. Unchanged or falling counts are
/// silent, but the baseline follows them, so 116 to 117 to 116 alerts on the last step.
/// </summary>
public static class OpeningPolicy
{
    public static OpeningEvaluation Evaluate(SeatBaseline? baseline, SeatObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (baseline is not null && baseline.Key != observation.Key)
            throw new ArgumentException("The baseline belongs to a different section.", nameof(baseline));

        // Attempt order, not wall-clock time, decides staleness; this also makes replays idempotent.
        if (baseline is not null && observation.AttemptSequence <= baseline.AttemptSequence)
            return new(EvaluationOutcome.IgnoredStale, baseline, null, baseline.Seats.ApparentSeats, null);

        var previous = baseline?.Seats.ApparentSeats;
        switch (observation.Kind)
        {
            case ObservationKind.Unknown:
                return new(EvaluationOutcome.IgnoredUnknown, baseline, null, previous, null);
            case ObservationKind.SemanticsUnverified:
                return new(EvaluationOutcome.RevalidationRequired, baseline, null, previous, null);
        }

        var seats = observation.Seats!.Value;
        var meaning = observation.Meaning!.Value;
        if (baseline is not null && baseline.Meaning != meaning)
            return new(EvaluationOutcome.RevalidationRequired, baseline, null, previous, null);

        var next = new SeatBaseline(observation.Key, seats, meaning, observation.AttemptSequence, observation.ObservedAt);
        var current = seats.ApparentSeats;

        if (baseline is null)
        {
            return current > 0
                ? new(EvaluationOutcome.Opening, next, Opening(OpeningReason.InitialAvailability, null), null, current)
                : new(EvaluationOutcome.BaselineEstablished, next, null, null, current);
        }

        return current > previous
            ? new(EvaluationOutcome.Opening, next, Opening(OpeningReason.IncreasedSeats, previous), previous, current)
            : new(EvaluationOutcome.NoOpening, next, null, previous, current);

        SectionOpening Opening(OpeningReason reason, int? prior) =>
            new(observation.Key, reason, prior, current, observation.AttemptSequence, observation.ObservedAt);
    }
}
