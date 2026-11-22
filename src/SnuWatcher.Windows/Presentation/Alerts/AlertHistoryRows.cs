using System.Globalization;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Core;

namespace SnuWatcher.Windows.Presentation.Alerts;

/// <summary>A historical opening and its separate current durable delivery projection.</summary>
public sealed record AlertHistoryRow
{
    /// <summary>Preserves the exact immutable trigger and current durable state.</summary>
    public AlertHistoryRow(AlertHistoryItem source) { ArgumentNullException.ThrowIfNull(source); Source = source; }
    /// <summary>Original typed facts; no source refresh or visibility inference is performed.</summary>
    public AlertHistoryItem Source { get; }
    /// <summary>Stable event identity, including when another event shares its ordinal.</summary>
    public Guid EventId => Source.EventId;
    /// <summary>Literal complete course code and unchanged section digits.</summary>
    public string Identifier => Source.Trigger.Key.CourseCode + " " + Source.Trigger.Key.Section;
    /// <summary>Historical apparent seats, never today's count.</summary>
    public string OriginalSeatsText => Source.Trigger.CurrentSeats.ToString(CultureInfo.InvariantCulture);
    /// <summary>Original source observation time.</summary>
    public string ObservedAtText => AlertHistoryText.At(Source.Trigger.ObservedAtUtc);
    /// <summary>Current durable state; Windows acceptance never means seen or registered.</summary>
    public string StatusKey => Source.Delivery.UnresolvedAttemptId is not null ? "Ui.AlertUnresolved" : AlertHistoryText.Status(Source.Delivery.Status);
    /// <summary>Original opening reasons; an increase is not proof that somebody dropped.</summary>
    public string ReasonKey => Source.Trigger.HasInitialReason && Source.Trigger.HasIncreaseReason ? "Ui.AlertBothReasons"
        : Source.Trigger.HasInitialReason ? "Ui.AlertInitialReason" : "Ui.AlertIncreaseReason";
}

/// <summary>Original association identity with an optional explicitly current watch label.</summary>
public sealed record AlertRecipientRow
{
    /// <summary>Preserves one original association.</summary>
    public AlertRecipientRow(AlertHistoryRecipient source) { ArgumentNullException.ThrowIfNull(source); Source = source; }
    /// <summary>Original subscription/generation/reason and optional current label.</summary>
    public AlertHistoryRecipient Source { get; }
    /// <summary>Current label or literal identity; neither is invented as an event-time label.</summary>
    public string WatchLabel => Source.CurrentWatchLabel ?? Source.SubscriptionId.ToString("N");
    /// <summary>Original reason for this association.</summary>
    public string ReasonKey => Source.Reason == OpeningReason.InitialAvailability ? "Ui.AlertInitialReason" : "Ui.AlertIncreaseReason";
}

/// <summary>One source-bucket record, never automatically another banner or a chronological timeline.</summary>
public sealed record AlertRecordRow
{
    /// <summary>Preserves the exact record source and coverage evidence.</summary>
    public AlertRecordRow(AlertHistoryAttempt source) { ArgumentNullException.ThrowIfNull(source); Source = source; }
    /// <summary>Typed associated batch context or event-result revision.</summary>
    public AlertHistoryAttempt Source { get; }
    /// <summary>Record bucket, explicitly distinct from wall-clock order.</summary>
    public string OriginKey => Source.Origin == AlertAttemptOrigin.BatchAttempt ? "Ui.AlertBatchAttempt" : "Ui.AlertEventResult";
    /// <summary>Coverage applies to this exact event only when explicitly recorded.</summary>
    public string ContextKey => Source.Origin == AlertAttemptOrigin.EventResult ? "Ui.AlertEventResultNotSubmission"
        : Source.CoversEvent ? "Ui.AlertCoversEvent" : "Ui.AlertAssociatedOnly";
    /// <summary>Durable native evidence or event-state evidence; unresolved never means accepted.</summary>
    public string OutcomeKey => Source.BatchAttempt is { } batch ? batch.Outcome switch
    {
        BatchAttemptResultOutcome.OsAccepted => "Ui.AlertOsAccepted",
        BatchAttemptResultOutcome.NotSubmitted => "Ui.AlertNotSubmitted",
        BatchAttemptResultOutcome.RetryableFailure => "Ui.AlertRetryable",
        BatchAttemptResultOutcome.InterruptedUnknown => "Ui.AlertAcceptanceUnknown",
        _ => "Ui.AlertUnresolved",
    } : AlertHistoryText.Status(Source.EventResult!.Status);
    /// <summary>Original recorded result time, or preparation time for unresolved batch authority.</summary>
    public string RecordedAtText => AlertHistoryText.At(Source.BatchAttempt is { } batch
        ? batch.RecordedAtUtc ?? batch.PreparedAtUtc : Source.EventResult!.RecordedAtUtc);
    /// <summary>Historical whole-batch submitted counts, absent for event-result revisions.</summary>
    public string? BatchCountsText => Source.BatchAttempt is { } batch
        ? string.Create(CultureInfo.InvariantCulture, $"{batch.Content.CurrentCounts.Registered}/{batch.Content.CurrentCounts.Capacity}") : null;
}

internal static class AlertHistoryText
{
    internal static string At(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    internal static string Status(DeliveryStatus status) => status switch
    {
        DeliveryStatus.OsAccepted => "Ui.AlertOsAccepted", DeliveryStatus.Failed => "Ui.AlertFailed",
        DeliveryStatus.Superseded => "Ui.AlertSuperseded", _ => "Ui.AlertPending",
    };
}
