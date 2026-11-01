namespace SnuWatcher.Core;

/// <summary>
/// The verified meaning of a published "registered/quota" pair. Two counts are only comparable
/// when they share a meaning; a changed meaning means the source must be revalidated, never guessed.
/// </summary>
public readonly record struct QuotaMeaning(string Id)
{
    /// <summary>"수강신청인원/정원(재학생)" / "Enrollment/Quota": enrolled students over the regular quota.</summary>
    public static QuotaMeaning EnrolledOverQuota { get; } = new("enrolled-over-quota");
}

/// <summary>Maps known bilingual source labels to one verified <see cref="QuotaMeaning"/>.</summary>
public static class QuotaLabels
{
    public static QuotaMeaning? Map(string? label)
    {
        if (label is null) return null;
        var normalized = string.Concat(label.Where(c => !char.IsWhiteSpace(c)));
        return normalized.Equals("수강신청인원/정원(재학생)", StringComparison.Ordinal)
            || normalized.Equals("Enrollment/Quota", StringComparison.OrdinalIgnoreCase)
            ? QuotaMeaning.EnrolledOverQuota
            : null;
    }
}

/// <summary>Registered and quota counts. Apparent seats never imply personal eligibility.</summary>
public readonly record struct SeatCount
{
    public SeatCount(int registered, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(registered);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Registered = registered;
        Capacity = capacity;
    }

    public int Registered { get; }
    public int Capacity { get; }

    /// <summary>max(0, quota - registered). Over-capacity and zero-capacity sections have none.</summary>
    public int ApparentSeats => Math.Max(0, Capacity - Registered);
}

public enum ObservationKind
{
    /// <summary>Complete counts with a verified quota meaning.</summary>
    Valid,
    /// <summary>No usable counts (outage, maintenance page, missing row, malformed text).</summary>
    Unknown,
    /// <summary>Counts were present but their label is not a verified quota meaning.</summary>
    SemanticsUnverified,
}

/// <summary>
/// One response for one section. <see cref="AttemptSequence"/> is a persisted, monotonically issued
/// number: wall-clock times alone cannot order responses after a clock correction.
/// </summary>
public sealed record SeatObservation
{
    private SeatObservation(SectionKey key, long attemptSequence, DateTimeOffset observedAt,
        ObservationKind kind, SeatCount? seats, QuotaMeaning? meaning)
    {
        Key = key;
        AttemptSequence = attemptSequence;
        ObservedAt = observedAt;
        Kind = kind;
        Seats = seats;
        Meaning = meaning;
    }

    public SectionKey Key { get; }
    public long AttemptSequence { get; }
    public DateTimeOffset ObservedAt { get; }
    public ObservationKind Kind { get; }
    public SeatCount? Seats { get; }
    public QuotaMeaning? Meaning { get; }

    public static SeatObservation Valid(SectionKey key, long attemptSequence, DateTimeOffset observedAt,
        SeatCount seats, QuotaMeaning meaning) =>
        new(key, attemptSequence, observedAt, ObservationKind.Valid, seats, meaning);

    public static SeatObservation Unknown(SectionKey key, long attemptSequence, DateTimeOffset observedAt) =>
        new(key, attemptSequence, observedAt, ObservationKind.Unknown, null, null);

    /// <summary>
    /// Builds an observation from raw source fields: missing counts are Unknown and an unmapped
    /// label is SemanticsUnverified. Never throws on hostile or malformed label text.
    /// </summary>
    public static SeatObservation FromSource(SectionKey key, long attemptSequence, DateTimeOffset observedAt,
        int? registered, int? capacity, string? quotaLabel)
    {
        if (registered is null or < 0 || capacity is null or < 0)
            return Unknown(key, attemptSequence, observedAt);
        var seats = new SeatCount(registered.Value, capacity.Value);
        return QuotaLabels.Map(quotaLabel) is { } meaning
            ? Valid(key, attemptSequence, observedAt, seats, meaning)
            : new(key, attemptSequence, observedAt, ObservationKind.SemanticsUnverified, seats, null);
    }
}
