using System.Collections.Immutable;

namespace SnuWatcher.Application.Presentation.Alerts;

/// <summary>Global, unfiltered event order. Ordinal-only cursors cannot identify a position within a shared issued check.</summary>
public sealed record AlertHistoryCursor
{
    public AlertHistoryCursor(long highWaterOrdinal, long afterOrdinal, Guid afterEventId, int version = 1)
    {
        AlertHistoryValidation.Version(version);
        HighWaterOrdinal = AlertHistoryValidation.Positive(highWaterOrdinal, nameof(highWaterOrdinal));
        AfterOrdinal = AlertHistoryValidation.Positive(afterOrdinal, nameof(afterOrdinal));
        AfterEventId = AlertHistoryValidation.Id(afterEventId, nameof(afterEventId));
        if (afterOrdinal > highWaterOrdinal) throw new ArgumentException("A history position cannot exceed its high water.", nameof(afterOrdinal));
    }
    public int Version => 1;
    public string Scope => "global-all";
    public long HighWaterOrdinal { get; }
    public long AfterOrdinal { get; }
    public Guid AfterEventId { get; }
}

/// <summary>Exclusive original-recipient association, ascending canonical Guid-N then activation generation, for one event.</summary>
public sealed record AlertRecipientCursor
{
    public AlertRecipientCursor(Guid eventId, Guid afterSubscriptionId, long afterGeneration, int version = 1)
    {
        AlertHistoryValidation.Version(version);
        EventId = AlertHistoryValidation.Id(eventId, nameof(eventId));
        AfterSubscriptionId = AlertHistoryValidation.Id(afterSubscriptionId, nameof(afterSubscriptionId));
        AfterGeneration = AlertHistoryValidation.Positive(afterGeneration, nameof(afterGeneration));
    }
    public int Version => 1;
    public string Scope => "event-recipients";
    public Guid EventId { get; }
    public Guid AfterSubscriptionId { get; }
    public long AfterGeneration { get; }
}

/// <summary>Record-source buckets, not an ordering of OS submissions or wall-clock chronology.</summary>
public enum AlertAttemptOrigin { BatchAttempt = 0, EventResult = 1 }

/// <summary>Batch attempts first by descending sequence, then event-result revisions descending, for one event context.</summary>
public sealed record AlertAttemptCursor
{
    public AlertAttemptCursor(Guid eventId, AlertAttemptOrigin origin, long afterSequence, int version = 1)
    {
        AlertHistoryValidation.Version(version);
        EventId = AlertHistoryValidation.Id(eventId, nameof(eventId));
        Origin = AlertHistoryValidation.Defined(origin, nameof(origin));
        AfterSequence = AlertHistoryValidation.Positive(afterSequence, nameof(afterSequence));
    }
    public int Version => 1;
    public string Scope => "event-records";
    public Guid EventId { get; }
    public AlertAttemptOrigin Origin { get; }
    public long AfterSequence { get; }
}

internal static class AlertHistoryValidation
{
    internal const int MaximumPageSize = 50;
    internal static Guid Id(Guid value, string name) => value != Guid.Empty ? value : throw new ArgumentException("An identity is required.", name);
    internal static long Positive(long value, string name) => value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    internal static long Nonnegative(long value, string name) => value >= 0 ? value : throw new ArgumentOutOfRangeException(name);
    internal static T Defined<T>(T value, string name) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(name);
    internal static void Version(int value) { if (value != 1) throw new ArgumentOutOfRangeException(nameof(value), "Unsupported history cursor version."); }
    internal static ImmutableArray<T> Items<T>(IEnumerable<T> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is ImmutableArray<T> { IsDefault: true }) throw new ArgumentException("The page input must be initialized.", nameof(source));
        var items = source.Take(MaximumPageSize + 1).ToImmutableArray();
        if (items.Length > MaximumPageSize || items.Any(item => item is null)) throw new ArgumentException("A page must be bounded and nonnull.", nameof(source));
        return items;
    }
    internal static int Compare(long leftOrdinal, Guid leftId, long rightOrdinal, Guid rightId) =>
        leftOrdinal != rightOrdinal ? leftOrdinal.CompareTo(rightOrdinal) : string.CompareOrdinal(leftId.ToString("N"), rightId.ToString("N"));
}
