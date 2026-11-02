using System.Diagnostics.CodeAnalysis;
using SnuWatcher.Application.Presentation.Alerts;

namespace SnuWatcher.Windows.Presentation.Alerts;

/// <summary>DTO self-consistency is distinct from binding a response to the requested exclusive position.</summary>
internal static class AlertContinuationValidation
{
    internal static void History([NotNull] AlertHistoryPage? page, AlertHistoryCursor? after)
    {
        if (page is null || page.Items.Length > 25 || after is not null && (page.HighWaterOrdinal != after.HighWaterOrdinal
            || page.Items.Any(item => item.Trigger.Ordinal > after.AfterOrdinal || item.Trigger.Ordinal == after.AfterOrdinal
                && Compare(item.EventId, after.AfterEventId) >= 0))) throw Invalid();
    }
    internal static void Recipients([NotNull] AlertRecipientPage? page, Guid eventId, AlertRecipientCursor? after)
    {
        if (page is null || page.EventId != eventId || page.Items.Length > 25 || after is not null && page.Items.Any(item =>
            Compare(item.SubscriptionId, after.AfterSubscriptionId) < 0 || item.SubscriptionId == after.AfterSubscriptionId
                && item.Generation <= after.AfterGeneration)) throw Invalid();
    }
    internal static void Attempts([NotNull] AlertAttemptPage? page, Guid eventId, AlertAttemptCursor? after)
    {
        if (page is null || page.EventId != eventId || page.Items.Length > 25 || after is not null && page.Items.Any(item =>
            item.Origin < after.Origin || item.Origin == after.Origin && item.Sequence >= after.AfterSequence)) throw Invalid();
    }
    private static int Compare(Guid first, Guid second) => string.CompareOrdinal(first.ToString("N"), second.ToString("N"));
    private static InvalidDataException Invalid() => new("The alert response does not match its bounded request.");
}
