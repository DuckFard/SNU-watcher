using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore : IWatchDashboardReader
{
    public Task<WatchPageReadResult> ReadWatchPageAsync(WatchPageCursor? after = null, int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > WatchPageSnapshot.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var stamp = ReadUiStamp(connection, transaction);
            if (after is not null && after.CatalogRevision != stamp.CatalogRevision)
                return new WatchPageReadResult(WatchPageReadDisposition.RestartRequired, stamp.CatalogRevision, null);
            List<UiDefinitionRow> rows = [];
            using (var query = Command(connection, transaction, """
                SELECT id,snapshot FROM Subscriptions
                WHERE id COLLATE BINARY>$after AND COALESCE(json_extract(snapshot,'$.status'),-1)<>$removed
                ORDER BY id COLLATE BINARY LIMIT $limit;
                """, ("$after", after?.AfterSubscriptionId.ToString("N") ?? ""), ("$removed", (int)WatchStatus.Removed), ("$limit", pageSize + 1)))
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                {
                    var json = reader.GetString(1);
                    var identity = StoreUiWire.SummaryIdentity(json);
                    if (identity.Id.ToString("N") != reader.GetString(0)) throw new InvalidDataException("The watch definition identity is inconsistent.");
                    rows.Add(new(identity.Id, identity.Generation, identity.Kind, json));
                }
            var hasMore = rows.Count > pageSize;
            var pageRows = rows.Take(pageSize).ToArray();
            var summaries = ReadUiSummaries(connection, transaction, pageRows);
            var next = hasMore ? new WatchPageCursor(stamp.CatalogRevision, pageRows[^1].Id) : null;
            var page = new WatchPageSnapshot(stamp, summaries, hasMore, next);
            transaction.Commit();
            return new WatchPageReadResult(WatchPageReadDisposition.Page, stamp.CatalogRevision, page);
        }, cancellationToken);
    }

    public Task<WatchDetailsSnapshot?> ReadWatchDetailsAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        if (subscriptionId == Guid.Empty) throw new ArgumentException("A watch identity is required.", nameof(subscriptionId));
        return _lane.ExecuteAsync<WatchDetailsSnapshot?>(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var stamp = ReadUiStamp(connection, transaction);
            var subscription = ReadSubscription(connection, transaction, subscriptionId);
            if (subscription is null || subscription.Status == WatchStatus.Removed) return null;
            if (subscription.Id != subscriptionId) throw new InvalidDataException("The watch definition identity is inconsistent.");
            List<SectionSnapshot> sections = [];
            using (var query = Command(connection, transaction, """
                SELECT m.sectionKey,s.snapshot FROM Members m LEFT JOIN SectionState s ON s.sectionKey=m.sectionKey
                WHERE m.subscriptionId=$id AND m.generation=$generation ORDER BY m.sectionKey LIMIT $limit;
                """, ("$id", subscriptionId.ToString("N")), ("$generation", subscription.ActivationGeneration), ("$limit", PersistenceLimits.MaxSections + 1)))
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                {
                    var key = StoreWire.ReadKey(reader.GetString(0));
                    var state = reader.IsDBNull(1) ? SectionSnapshot.Unchecked(key) : StoreWire.ReadSection(reader.GetString(1));
                    if (state.Key != key) throw new InvalidDataException("The section state identity is inconsistent.");
                    sections.Add(state);
                }
            var result = new WatchDetailsSnapshot(stamp, subscription, sections);
            transaction.Commit();
            return result;
        }, cancellationToken);
    }

    public Task<SourceStatusSnapshot> ReadSourceStatusAsync(CancellationToken cancellationToken = default) =>
        _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var stamp = ReadUiStamp(connection, transaction);
            var policy = ReadSourcePolicy(connection, transaction, SourcePolicyIds.SnuSugang);
            CompletedTransportEvidence? evidence = null;
            if (policy.Progress.LatestCompletedOrdinal > 0)
            {
                using var query = Command(connection, transaction, """
                    SELECT r.id,c.sourceFacts FROM SourceRequests r JOIN SourceRequestResults c ON c.requestId=r.id
                    WHERE r.sourceId=$source AND r.ordinal=$ordinal;
                    """, ("$source", policy.SourceId), ("$ordinal", policy.Progress.LatestCompletedOrdinal));
                using var reader = query.ExecuteReader();
                if (reader.Read()) evidence = StoreUiWire.Transport(reader.GetString(1), reader.GetString(0), policy);
            }
            var result = new SourceStatusSnapshot(stamp, policy, evidence);
            transaction.Commit();
            return result;
        }, cancellationToken);

    private UiReadStamp ReadUiStamp(SqliteConnection connection, SqliteTransaction transaction) =>
        new(Metadata(connection, transaction, "catalogRevision"), Metadata(connection, transaction, "issuedOrdinal"), _time.GetUtcNow());

    private static ImmutableArray<WatchDefinitionSummary> ReadUiSummaries(SqliteConnection connection, SqliteTransaction transaction, UiDefinitionRow[] rows)
    {
        if (rows.Length == 0) return [];
        List<(string Name, object? Value)> parameters = [];
        List<string> tuples = [];
        for (var index = 0; index < rows.Length; index++)
        {
            tuples.Add($"($id{index},$generation{index},$kind{index})");
            parameters.Add(($"$id{index}", rows[index].Id.ToString("N")));
            parameters.Add(($"$generation{index}", rows[index].Generation));
            parameters.Add(($"$kind{index}", (int)rows[index].Kind));
        }
        // Aggregate only current-generation identities. A collapsed 2,000-member group never
        // materializes its members or section JSON merely to paint one dashboard row.
        var sql = "WITH wanted(id,generation,kind) AS(VALUES " + string.Join(',', tuples) + "), facts AS(" + """
            SELECT w.id,w.kind,count(m.sectionKey) AS members,min(m.sectionKey) AS firstKey,
                min(i.year) AS minYear,max(i.year) AS maxYear,min(i.semester) AS minSemester,max(i.semester) AS maxSemester,
                min(i.detail) AS minDetail,max(i.detail) AS maxDetail
            FROM wanted w LEFT JOIN Members m ON m.subscriptionId=w.id AND m.generation=w.generation
            LEFT JOIN SectionIdentities i ON i.sectionKey=m.sectionKey GROUP BY w.id,w.kind)
            SELECT f.id,f.members,f.firstKey,f.minYear,f.maxYear,f.minSemester,f.maxSemester,f.minDetail,f.maxDetail,s.snapshot
            FROM facts f LEFT JOIN SectionState s ON f.kind=0 AND s.sectionKey=f.firstKey;
            """;
        Dictionary<Guid, (SectionKey Key, int Count, SectionSnapshot? State)> facts = [];
        using (var query = Command(connection, transaction, sql, parameters.ToArray()))
        using (var reader = query.ExecuteReader())
            while (reader.Read())
            {
                var count = reader.GetInt64(1);
                if (count is < 1 or > PersistenceLimits.MaxSections || reader.IsDBNull(2)) throw new InvalidDataException("The watch membership exceeds its supported shape.");
                var key = StoreWire.ReadKey(reader.GetString(2));
                if (reader.GetInt32(3) != key.Year || reader.GetInt32(4) != key.Year
                    || reader.GetString(5) != key.SemesterCode || reader.GetString(6) != key.SemesterCode
                    || reader.GetString(7) != key.DetailSemesterCode || reader.GetString(8) != key.DetailSemesterCode)
                    throw new InvalidDataException("The watch membership spans inconsistent academic terms.");
                var state = reader.IsDBNull(9) ? null : StoreWire.ReadSection(reader.GetString(9));
                if (state is not null && state.Key != key) throw new InvalidDataException("The section state identity is inconsistent.");
                facts.Add(Guid.ParseExact(reader.GetString(0), "N"), (key, (int)count, state));
            }
        return rows.Select(row =>
        {
            var value = facts[row.Id];
            return StoreUiWire.Summary(row.Json, value.Key, value.Count,
                row.Kind == SubscriptionKind.Section ? value.State ?? SectionSnapshot.Unchecked(value.Key) : null);
        }).ToImmutableArray();
    }
    private sealed record UiDefinitionRow(Guid Id, long Generation, SubscriptionKind Kind, string Json);
}
