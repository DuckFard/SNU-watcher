using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery;

/// <summary>
/// Revalidates a pending event against current counts and watch state. The caller must read this
/// context and begin any OS send under its lifecycle/send gate, outside SQL transactions. A Send
/// result alone cannot prevent a subsequent pause or removal race.
/// </summary>
public static class DeliveryPlanner
{
    public static DeliveryDecision Plan(DeliveryContext context, DateTimeOffset now, TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (maxAge <= TimeSpan.Zero || maxAge > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Maximum observation age must be positive and at most one day.");
        if (context.Subscriptions.Select(s => s.Id).Distinct().Count() != context.Subscriptions.Length)
            throw new ArgumentException("Delivery context subscriptions must have unique identities.", nameof(context));
        if (context.Delivery.Status != DeliveryStatus.Pending)
            return Decision(DeliveryDisposition.Terminal, "terminal");

        var recipients = EligibleRecipients(context, now);
        if (recipients.IsEmpty) return Decision(DeliveryDisposition.Superseded, "no_eligible_recipients");
        if (context.Section.Baseline is not { } current)
            return Decision(DeliveryDisposition.NeedsFreshCheck, "missing_current_counts");
        // A preserved valid baseline does not turn a newer unknown/error attempt into current data.
        if (context.Section.LatestOutcome != CheckOutcome.Success)
            return Decision(DeliveryDisposition.NeedsFreshCheck, "latest_check_unavailable");
        if (current.AttemptSequence < context.Delivery.Trigger.AttemptSequence)
            return Decision(DeliveryDisposition.NeedsFreshCheck, "source_before_trigger");
        if (current.ObservedAt > now)
            return Decision(DeliveryDisposition.NeedsFreshCheck, "clock_changed");
        // A verified full observation closes the old opening even if it later ages. Unknown data
        // never reaches this branch. A subsequent fresh increase will create its own event.
        if (current.Seats.ApparentSeats == 0)
            return Decision(DeliveryDisposition.Superseded, "section_full");
        if (now - current.ObservedAt > maxAge)
            return Decision(DeliveryDisposition.NeedsFreshCheck, "source_stale");

        return DeliveryDecision.Ready(new DeliveryEnvelope(context.Delivery, current, recipients));
    }

    private static ImmutableArray<Recipient> EligibleRecipients(DeliveryContext context, DateTimeOffset now)
    {
        var subscriptions = context.Subscriptions.ToDictionary(s => s.Id);
        return context.Delivery.Trigger.Recipients.Where(recipient =>
            subscriptions.TryGetValue(Guid.ParseExact(recipient.SubscriptionId, "N"), out var watch)
            && watch.Status == WatchStatus.Active && watch.ActivationGeneration == recipient.Generation
            && (watch.Expiry is null || !watch.Expiry.IsExpiredAt(now))
            && watch.Members.Any(member => member.Key == context.Delivery.Key)).ToImmutableArray();
    }

    private static DeliveryDecision Decision(DeliveryDisposition disposition, string reasonCode) =>
        DeliveryDecision.WithoutEnvelope(disposition, reasonCode);
}
