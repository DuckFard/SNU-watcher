namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>Fixed SQL only. Later-page high-water validation precedes the complete tuple seek.</summary>
internal static class StoreHistoryQueries
{
    internal const string FirstIds = """
        SELECT e.id,e.triggerOrdinal FROM Events e WHERE e.triggerOrdinal<=$high
        ORDER BY e.triggerOrdinal DESC,e.id COLLATE BINARY DESC LIMIT $take
        """;
    internal const string LaterIds = """
        SELECT e.id,e.triggerOrdinal FROM Events e WHERE (e.triggerOrdinal,e.id)<($ordinal,$id)
        ORDER BY e.triggerOrdinal DESC,e.id COLLATE BINARY DESC LIMIT $take
        """;
    internal const string DetailId = "SELECT id,triggerOrdinal FROM Events WHERE id=$event LIMIT 1";
    internal static string Summary(string ids, bool continuing) => "WITH page AS MATERIALIZED (" + ids + ") " + """
        SELECT e.id,e.sectionKey,e.triggerOrdinal,e.trigger,e.createdUtc,o.revision,o.status,o.attempts,
            m.batchId,b.sectionKey,CASE WHEN f.eventId IS NULL THEN NULL ELSE u.id END,
            d.revision,d.status,d.attempts,d.recordedUtc,
            (SELECT count(*) FROM EventRecipients r WHERE r.eventId=e.id),
            (SELECT min(reason) FROM EventRecipients r WHERE r.eventId=e.id),
            (SELECT max(reason) FROM EventRecipients r WHERE r.eventId=e.id),
        """ + (continuing ? "(SELECT count(*) FROM page)>$size " : "0 ") + """
        FROM page p JOIN Events e ON e.id=p.id
        LEFT JOIN Outbox o ON o.eventId=e.id
        LEFT JOIN DeliveryBatchEvents m ON m.eventId=e.id LEFT JOIN DeliveryBatches b ON b.id=m.batchId
        LEFT JOIN DeliveryBatchAttempts u ON u.batchId=m.batchId AND u.status=0
        LEFT JOIN DeliveryBatchAttemptEvents f ON f.attemptId=u.id AND f.eventId=e.id
        LEFT JOIN DeliveryAttempts d ON d.eventId=e.id AND d.revision=
            (SELECT max(revision) FROM DeliveryAttempts x WHERE x.eventId=e.id)
        ORDER BY p.triggerOrdinal DESC,p.id COLLATE BINARY DESC LIMIT $size;
        """;

    internal const string Recipients = """
        SELECT r.subscriptionId,r.generation,r.reason,s.snapshot
        FROM EventRecipients r LEFT JOIN Subscriptions s ON s.id=r.subscriptionId
        WHERE r.eventId=$event
        """;
    internal const string LaterRecipient = " AND (r.subscriptionId,r.generation)>($subscription,$generation) ";
    internal const string RecipientOrder = " ORDER BY r.subscriptionId COLLATE BINARY,r.generation LIMIT $take;";
    internal const string AttemptContext = """
        SELECT e.sectionKey,m.batchId,b.sectionKey FROM Events e
        LEFT JOIN DeliveryBatchEvents m ON m.eventId=e.id LEFT JOIN DeliveryBatches b ON b.id=m.batchId
        WHERE e.id=$event;
        """;
    internal static string BatchRecords(bool continuing) => """
        SELECT a.id,a.batchId,a.sequence,a.status,a.snapshot,r.sourceFacts,r.receipt,
            EXISTS(SELECT 1 FROM DeliveryBatchAttemptEvents f WHERE f.attemptId=a.id AND f.eventId=$event)
        FROM DeliveryBatchAttempts a LEFT JOIN DeliveryBatchResults r ON r.attemptId=a.id
        WHERE a.batchId=$batch
        """ + (continuing ? " AND a.sequence<$sequence " : " ") + "ORDER BY a.sequence DESC LIMIT $take;";
    internal static string EventRecords(bool continuing) => """
        SELECT revision,status,attempts,recordedUtc FROM DeliveryAttempts
        WHERE eventId=$event
        """ + (continuing ? " AND revision<$sequence " : " ") + "ORDER BY revision DESC LIMIT $take;";
}
