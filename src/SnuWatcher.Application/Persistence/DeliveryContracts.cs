using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

public enum DeliveryStatus { Pending, OsAccepted, Failed, Superseded }
public enum DeliveryWriteResult { Applied, VersionConflict, NotFound }

/// <summary>Immutable trigger facts and stable dedupe identity; never evidence of a banner or registration.</summary>
public sealed record PendingDelivery
{
    public PendingDelivery(Guid eventId, long deliveryRevision, OpeningNotification trigger, DeliveryStatus status,
        int attempts, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        PersistenceValidation.Notification(trigger);
        EventId = PersistenceValidation.Id(eventId, nameof(eventId));
        DeliveryRevision = PersistenceValidation.Positive(deliveryRevision, nameof(deliveryRevision));
        Status = PersistenceValidation.Defined(status, nameof(status));
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        Attempts = attempts;
        Trigger = trigger;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public Guid EventId { get; }
    public long DeliveryRevision { get; }
    public OpeningNotification Trigger { get; }
    public SectionKey Key => Trigger.Key;
    public string NativeDedupeKey => EventId.ToString("N");
    public DeliveryStatus Status { get; }
    public int Attempts { get; }
    public DateTimeOffset CreatedAtUtc { get; }
}

/// <summary>Current state to revalidate under the lifecycle/send gate, outside any SQL transaction.</summary>
public sealed record DeliveryContext
{
    public DeliveryContext(PendingDelivery delivery, SectionSnapshot section, IEnumerable<SubscriptionSnapshot> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(section);
        if (delivery.Key != section.Key) throw new ArgumentException("Delivery and current state must identify the same section.", nameof(section));
        Delivery = delivery;
        Section = section;
        Subscriptions = PersistenceValidation.Snapshot(subscriptions, PersistenceLimits.MaxSections, nameof(subscriptions));
    }

    public PendingDelivery Delivery { get; }
    public SectionSnapshot Section { get; }
    public ImmutableArray<SubscriptionSnapshot> Subscriptions { get; }
}

public sealed record DeliveryResultCommand
{
    public DeliveryResultCommand(Guid eventId, long expectedRevision, DeliveryStatus status, int attempts, DateTimeOffset recordedAtUtc)
    {
        EventId = PersistenceValidation.Id(eventId, nameof(eventId));
        ExpectedRevision = PersistenceValidation.Positive(expectedRevision, nameof(expectedRevision));
        Status = PersistenceValidation.Defined(status, nameof(status));
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        Attempts = attempts;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
    }

    public Guid EventId { get; }
    public long ExpectedRevision { get; }
    public DeliveryStatus Status { get; }
    public int Attempts { get; }
    public DateTimeOffset RecordedAtUtc { get; }
}
