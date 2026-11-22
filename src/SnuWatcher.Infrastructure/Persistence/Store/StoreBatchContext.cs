using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    public Task<BatchDeliveryContext> ReadDeliveryBatchContextAsync(Guid batchId, BatchDeliveryPolicy policy,
        DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
    {
        BatchId(batchId); ArgumentNullException.ThrowIfNull(policy);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var batch = FindBatch(connection, transaction, batchId) ?? throw new KeyNotFoundException("That delivery batch does not exist.");
            var result = ReadBatchContext(connection, transaction, batch, policy, evaluatedAtUtc);
            transaction.Commit();
            return result;
        }, cancellationToken);
    }

    private static BatchDeliveryContext ReadBatchContext(SqliteConnection c, SqliteTransaction t,
        DeliveryBatchSnapshot batch, BatchDeliveryPolicy policy, DateTimeOffset now)
    {
        using var allHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var eligibleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        allHash.AppendData("snu-batch-facts-v1\n"u8); eligibleHash.AppendData("snu-batch-eligible-v1\n"u8);
        Dictionary<Guid, SubscriptionSnapshot?> cache = [];
        Dictionary<(Guid, long), HashSet<OpeningReason>> union = [];
        Dictionary<Guid, SubscriptionSnapshot> watches = [];
        long pending = 0, ineligible = 0, exhausted = 0, count = 0, minimum = 0, maximum = 0;
        DateTimeOffset? boundary = null;
        var overflow = false;
        foreach (var row in ReadPendingBatchEvents(c, t, batch.Id))
        {
            pending = checked(pending + 1);
            var associations = EligibleBatchAssociations(c, t, row, batch.Key, now, cache);
            var category = associations.Count == 0 ? 0 : row.Attempts >= policy.MaxAttempts ? 1 : 2;
            var facts = JsonSerializer.Serialize(new
            {
                version = 1, id = row.Id.ToString("N"), revision = row.Revision, ordinal = row.Ordinal, attempts = row.Attempts,
                trigger = StoreWire.Notification(row.Trigger), category,
                eligible = associations.OrderBy(a => a.Recipient.SubscriptionId, StringComparer.Ordinal).ThenBy(a => a.Recipient.Generation)
                    .Select(a => new { id = a.Recipient.SubscriptionId, generation = a.Recipient.Generation, reason = (int)a.Recipient.Reason }),
            });
            AppendBatchHash(allHash, facts);
            if (category == 0) { ineligible++; continue; }
            if (category == 1) { exhausted++; continue; }
            count = checked(count + 1);
            if (minimum == 0) minimum = row.Ordinal;
            maximum = row.Ordinal;
            AppendBatchHash(eligibleHash, facts);
            foreach (var association in associations)
            {
                var watch = association.Watch;
                if (watch.Expiry is { } expiry && (boundary is null || expiry.ExclusiveUtc < boundary)) boundary = expiry.ExclusiveUtc;
                if (overflow) continue;
                var identity = (watch.Id, association.Recipient.Generation);
                if (!union.TryGetValue(identity, out var reasons))
                {
                    if (union.Count == BatchDeliveryLimits.MaxRecipients) { overflow = true; continue; }
                    reasons = []; union.Add(identity, reasons); watches.TryAdd(watch.Id, watch);
                }
                reasons.Add(association.Recipient.Reason);
            }
        }
        var coverage = new BatchCoverageSummary(count, Convert.ToHexStringLower(eligibleHash.GetHashAndReset()), minimum, maximum);
        return new(batch, ReadSection(c, t, batch.Key), Metadata(c, t, "catalogRevision"), policy, now, boundary,
            pending, ineligible, exhausted, coverage, Convert.ToHexStringLower(allHash.GetHashAndReset()),
            overflow ? [] : union.Select(pair => new BatchRecipient(pair.Key.Item1, pair.Key.Item2, pair.Value)),
            overflow ? [] : watches.Values, UnresolvedBatchAttempt(c, t, batch.Id), overflow);
    }

    private static List<BatchAssociation> EligibleBatchAssociations(SqliteConnection c, SqliteTransaction t,
        BatchEventRow row, SectionKey key, DateTimeOffset now, Dictionary<Guid, SubscriptionSnapshot?> cache)
    {
        List<BatchAssociation> result = [];
        foreach (var recipient in row.Trigger.Recipients)
        {
            var id = Guid.ParseExact(recipient.SubscriptionId, "N");
            if (!cache.TryGetValue(id, out var watch))
            {
                watch = ReadSubscription(c, t, id);
                if (cache.Count < BatchDeliveryLimits.MaxRecipients) cache.Add(id, watch);
            }
            if (watch is { Status: WatchStatus.Active } && watch.ActivationGeneration == recipient.Generation
                && (watch.Expiry is null || !watch.Expiry.IsExpiredAt(now)) && watch.Members.Any(m => m.Key == key))
                result.Add(new(recipient, watch));
        }
        return result;
    }

    // Close each bounded reader before yielding. Result/seal callers may then write outbox rows safely;
    // never mutate a SELECT's own pending-status predicate while its reader is still stepping.
    private static IEnumerable<BatchEventRow> ReadPendingBatchEvents(SqliteConnection c, SqliteTransaction t, Guid batchId)
    {
        long after = 0;
        while (true)
        {
            List<BatchEventRow> page = [];
            using (var query = Command(c, t, """
                SELECT e.id,o.revision,o.attempts,e.triggerOrdinal,e.trigger FROM DeliveryBatchEvents m
                JOIN Events e ON e.id=m.eventId JOIN Outbox o ON o.eventId=e.id
                WHERE m.batchId=$batch AND o.status=$pending AND e.triggerOrdinal>$after ORDER BY e.triggerOrdinal LIMIT $limit;
                """, ("$batch", batchId.ToString("N")), ("$pending", (int)DeliveryStatus.Pending),
                ("$after", after), ("$limit", BatchDeliveryLimits.MaxPageSize)))
            using (var reader = query.ExecuteReader())
                while (reader.Read()) page.Add(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetInt64(1),
                    reader.GetInt32(2), reader.GetInt64(3), StoreWire.ReadNotification(reader.GetString(4))));
            if (page.Count == 0) yield break;
            after = page[^1].Ordinal;
            foreach (var row in page) yield return row;
        }
    }

    private static void AppendBatchHash(IncrementalHash hash, string facts)
    { hash.AppendData(Encoding.UTF8.GetBytes(facts)); hash.AppendData("\n"u8); }
    private sealed record BatchEventRow(Guid Id, long Revision, int Attempts, long Ordinal, OpeningNotification Trigger);
    private sealed record BatchAssociation(Recipient Recipient, SubscriptionSnapshot Watch);
}
