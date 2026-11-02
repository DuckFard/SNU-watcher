using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery;

public enum DeliveryDisposition { Send, NeedsFreshCheck, Superseded, Terminal }

/// <summary>A pure decision, with a bounded diagnostic code and an envelope only when sending is eligible.</summary>
public sealed record DeliveryDecision
{
    private DeliveryDecision(DeliveryDisposition disposition, string reasonCode, DeliveryEnvelope? envelope)
    {
        Disposition = disposition;
        ReasonCode = reasonCode;
        Envelope = envelope;
    }

    public DeliveryDisposition Disposition { get; }
    public string ReasonCode { get; }
    public DeliveryEnvelope? Envelope { get; }

    internal static DeliveryDecision Ready(DeliveryEnvelope envelope) => new(DeliveryDisposition.Send, "ready", envelope);

    internal static DeliveryDecision WithoutEnvelope(DeliveryDisposition disposition, string reasonCode) =>
        new(disposition, reasonCode, null);
}

/// <summary>
/// Content for one delivery attempt: current verified counts and eligible recipients, with original
/// immutable trigger facts retained separately. Event identity is stable across retries; this is not
/// evidence of OS acceptance, a visible banner or successful registration.
/// </summary>
public sealed record DeliveryEnvelope
{
    internal DeliveryEnvelope(PendingDelivery delivery, SeatBaseline current, ImmutableArray<Recipient> recipients)
    {
        EventId = delivery.EventId;
        DeliveryRevision = delivery.DeliveryRevision;
        Key = current.Key;
        CurrentCounts = current.Seats;
        CurrentSuccessAtUtc = current.ObservedAt.ToUniversalTime();
        CurrentAttemptOrdinal = current.AttemptSequence;
        EligibleRecipients = recipients;
        Trigger = delivery.Trigger;
    }

    public Guid EventId { get; }
    public string NativeDedupeKey => EventId.ToString("N");
    public long DeliveryRevision { get; }
    public SectionKey Key { get; }
    public SeatCount CurrentCounts { get; }
    public int CurrentSeats => CurrentCounts.ApparentSeats;
    public DateTimeOffset CurrentSuccessAtUtc { get; }
    public long CurrentAttemptOrdinal { get; }
    public ImmutableArray<Recipient> EligibleRecipients { get; }
    public OpeningNotification Trigger { get; }
}
