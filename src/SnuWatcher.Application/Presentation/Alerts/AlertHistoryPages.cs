using System.Collections.Immutable;
using SnuWatcher.Application.Presentation.Reads;

namespace SnuWatcher.Application.Presentation.Alerts;

/// <summary>A bounded global event page at one consistent read; its high water does not freeze future late completions.</summary>
public sealed record AlertHistoryPage
{
    public AlertHistoryPage(UiReadStamp stamp, long highWaterOrdinal, IEnumerable<AlertHistoryItem> items,
        bool hasMore, AlertHistoryCursor? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        HighWaterOrdinal = AlertHistoryValidation.Nonnegative(highWaterOrdinal, nameof(highWaterOrdinal));
        Items = AlertHistoryValidation.Items(items);
        if (highWaterOrdinal > stamp.IssuedCheckOrdinal || Items.Any(item => item.Trigger.Ordinal > highWaterOrdinal))
            throw new ArgumentException("An event page cannot exceed its captured issued high water.");
        for (var index = 1; index < Items.Length; index++)
            if (AlertHistoryValidation.Compare(Items[index - 1].Trigger.Ordinal, Items[index - 1].EventId,
                Items[index].Trigger.Ordinal, Items[index].EventId) <= 0) throw new ArgumentException("Event positions must be unique and descending.");
        if (hasMore != (nextCursor is not null) || hasMore && (Items.IsEmpty || nextCursor!.HighWaterOrdinal != highWaterOrdinal
            || nextCursor.AfterEventId != Items[^1].EventId || nextCursor.AfterOrdinal != Items[^1].Trigger.Ordinal))
            throw new ArgumentException("A continuation must identify this page's exact last tuple.");
        Stamp = stamp; HasMore = hasMore; NextCursor = nextCursor;
    }
    public const int MaximumPageSize = 50;
    public UiReadStamp Stamp { get; }
    public long HighWaterOrdinal { get; }
    public ImmutableArray<AlertHistoryItem> Items { get; }
    public bool HasMore { get; }
    public AlertHistoryCursor? NextCursor { get; }
}

/// <summary>Bounded original recipients. Current labels can change between independent page reads.</summary>
public sealed record AlertRecipientPage
{
    public AlertRecipientPage(Guid eventId, UiReadStamp stamp, IEnumerable<AlertHistoryRecipient> items,
        bool hasMore, AlertRecipientCursor? nextCursor)
    {
        EventId = AlertHistoryValidation.Id(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(stamp); Items = AlertHistoryValidation.Items(items);
        for (var index = 1; index < Items.Length; index++)
        {
            var previous = Items[index - 1]; var current = Items[index];
            var compared = string.CompareOrdinal(previous.SubscriptionId.ToString("N"), current.SubscriptionId.ToString("N"));
            if (compared > 0 || compared == 0 && previous.Generation >= current.Generation)
                throw new ArgumentException("Recipient positions must be unique and ascending.");
        }
        if (hasMore != (nextCursor is not null) || hasMore && (Items.IsEmpty || nextCursor!.EventId != eventId
            || nextCursor.AfterSubscriptionId != Items[^1].SubscriptionId || nextCursor.AfterGeneration != Items[^1].Generation))
            throw new ArgumentException("A recipient continuation must retain its exact event and association.");
        Stamp = stamp; HasMore = hasMore; NextCursor = nextCursor;
    }
    public Guid EventId { get; }
    public UiReadStamp Stamp { get; }
    public ImmutableArray<AlertHistoryRecipient> Items { get; }
    public bool HasMore { get; }
    public AlertRecipientCursor? NextCursor { get; }
}

/// <summary>Batch-attempt sequence descending, then event-result revision descending; never global wall-clock chronology.</summary>
public sealed record AlertAttemptPage
{
    public AlertAttemptPage(Guid eventId, UiReadStamp stamp, IEnumerable<AlertHistoryAttempt> items,
        bool hasMore, AlertAttemptCursor? nextCursor)
    {
        EventId = AlertHistoryValidation.Id(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(stamp); Items = AlertHistoryValidation.Items(items);
        if (Items.Any(item => item.EventId != eventId)) throw new ArgumentException("Attempt contexts must identify this event.");
        for (var index = 1; index < Items.Length; index++)
            if (Items[index - 1].Origin > Items[index].Origin || Items[index - 1].Origin == Items[index].Origin
                && Items[index - 1].Sequence <= Items[index].Sequence)
                throw new ArgumentException("Record-source buckets and within-source positions must be ordered and unique.");
        if (hasMore != (nextCursor is not null) || hasMore && (Items.IsEmpty || nextCursor!.EventId != eventId
            || nextCursor.Origin != Items[^1].Origin || nextCursor.AfterSequence != Items[^1].Sequence))
            throw new ArgumentException("An attempt continuation must retain its exact event and record source.");
        Stamp = stamp; HasMore = hasMore; NextCursor = nextCursor;
    }
    public Guid EventId { get; }
    public UiReadStamp Stamp { get; }
    public ImmutableArray<AlertHistoryAttempt> Items { get; }
    public bool HasMore { get; }
    public AlertAttemptCursor? NextCursor { get; }
}
