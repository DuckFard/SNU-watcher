namespace SnuWatcher.Application.Delivery.Batches;

/// <summary>
/// Transient validity window around an unchanged sealed envelope; never persisted as attempt authority.
/// The driver rechecks this complete window immediately before Show after any owned queue/probe work.
/// Crossing the earliest recipient expiry refuses the whole submission; a fresh store read recomputes its union.
/// </summary>
public sealed record BatchDesktopSubmission
{
    public BatchDesktopSubmission(BatchDeliveryEnvelope envelope, DateTimeOffset validatedAtUtc,
        DateTimeOffset? eligibilityExpiresAtUtc, TimeSpan maxSourceAge)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (maxSourceAge <= TimeSpan.Zero || maxSourceAge > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(maxSourceAge));
        if (envelope.CurrentSuccessAtUtc > validatedAtUtc || validatedAtUtc - envelope.CurrentSuccessAtUtc > maxSourceAge
            || eligibilityExpiresAtUtc is { } expiry && expiry <= validatedAtUtc)
            throw new ArgumentException("A native submission requires a current, unexpired validity window.");
        Envelope = envelope;
        ValidatedAtUtc = validatedAtUtc.ToUniversalTime();
        EligibilityExpiresAtUtc = eligibilityExpiresAtUtc?.ToUniversalTime();
        MaxSourceAge = maxSourceAge;
    }

    public BatchDeliveryEnvelope Envelope { get; }
    public DateTimeOffset ValidatedAtUtc { get; }
    public DateTimeOffset? EligibilityExpiresAtUtc { get; }
    public TimeSpan MaxSourceAge { get; }
}
