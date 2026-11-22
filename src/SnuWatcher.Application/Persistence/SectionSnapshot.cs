using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

/// <summary>Completion health/watermark and last valid baseline are independent persisted state.</summary>
public sealed record SectionSnapshot
{
    public SectionSnapshot(SectionKey key, long version, long latestCompletedOrdinal, CheckOutcome? latestOutcome,
        DateTimeOffset? latestAttemptAtUtc, DateTimeOffset? lastSuccessfulAtUtc, SeatBaseline? baseline)
    {
        ArgumentNullException.ThrowIfNull(key);
        Version = PersistenceValidation.Nonnegative(version, nameof(version));
        LatestCompletedOrdinal = PersistenceValidation.Nonnegative(latestCompletedOrdinal, nameof(latestCompletedOrdinal));
        if (latestOutcome is { } outcome) PersistenceValidation.Defined(outcome, nameof(latestOutcome));
        if (latestCompletedOrdinal == 0 && (version != 0 || latestOutcome is not null || latestAttemptAtUtc is not null || lastSuccessfulAtUtc is not null || baseline is not null))
            throw new ArgumentException("An unchecked section cannot contain observation state.");
        if (latestCompletedOrdinal > 0 && (version == 0 || latestOutcome is null || latestAttemptAtUtc is null))
            throw new ArgumentException("A completed section needs version and health state.");
        if (baseline is not null && (baseline.Key != key || baseline.AttemptSequence <= 0 || baseline.AttemptSequence > latestCompletedOrdinal))
            throw new ArgumentException("Baseline identity/order must match section state.", nameof(baseline));
        if ((baseline is null) != (lastSuccessfulAtUtc is null)
            || (baseline is not null && baseline.ObservedAt != lastSuccessfulAtUtc))
            throw new ArgumentException("Last success must identify the valid baseline observation.", nameof(lastSuccessfulAtUtc));
        if (latestOutcome == CheckOutcome.Success && (baseline is null || baseline.AttemptSequence != latestCompletedOrdinal || baseline.ObservedAt != latestAttemptAtUtc))
            throw new ArgumentException("Successful health must identify the latest completed valid observation.", nameof(latestOutcome));
        Key = key;
        LatestOutcome = latestOutcome;
        LatestAttemptAtUtc = latestAttemptAtUtc?.ToUniversalTime();
        LastSuccessfulAtUtc = lastSuccessfulAtUtc?.ToUniversalTime();
        Baseline = baseline;
    }

    public SectionKey Key { get; }
    public long Version { get; }
    public long LatestCompletedOrdinal { get; }
    public CheckOutcome? LatestOutcome { get; }
    public DateTimeOffset? LatestAttemptAtUtc { get; }
    public DateTimeOffset? LastSuccessfulAtUtc { get; }
    public SeatBaseline? Baseline { get; }

    public static SectionSnapshot Unchecked(SectionKey key) => new(key, 0, 0, null, null, null, null);
}
