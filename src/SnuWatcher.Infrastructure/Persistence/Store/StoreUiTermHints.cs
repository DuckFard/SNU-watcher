using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Presentation.Terms;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore : ITermHintCache
{
    Task<TermHintCacheSnapshot> ITermHintCache.ReadAsync(CancellationToken cancellationToken) =>
        _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var snapshot = ReadUiTermHints(connection, transaction);
            transaction.Commit();
            return snapshot;
        }, cancellationToken);

    public Task<TermHintWriteResult> TryReplaceYearAsync(ReplaceTermHintsCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var current = ReadUiTermHints(connection, transaction);
            if (current.Revision != command.ExpectedCacheRevision)
                return new TermHintWriteResult(TermHintWriteStatus.VersionConflict, current);
            var terms = command.Terms.Select(term => new CachedTermHint(term.Selector, term.Name, term.EnglishName)).ToArray();
            var json = StoreUiWire.Terms(terms);
            var previous = current.Years.FirstOrDefault(year => year.Year == command.Year);
            if (previous is not null && previous.FetchedAtUtc == command.FetchedAtUtc && StoreUiWire.Terms(previous.Terms) == json)
                return new TermHintWriteResult(TermHintWriteStatus.NoChange, current);
            var revision = checked(current.Revision + 1);
            Run(connection, transaction, "INSERT INTO UiTermHints(year,cacheRevision,fetchedUtc,hintsJson) VALUES($year,$revision,$fetched,$hints) "
                + "ON CONFLICT(year) DO UPDATE SET cacheRevision=excluded.cacheRevision,fetchedUtc=excluded.fetchedUtc,hintsJson=excluded.hintsJson;",
                ("$year", command.Year), ("$revision", revision), ("$fetched", StoreWire.At(command.FetchedAtUtc)), ("$hints", json));
            // Write order, rather than UTC or read activity, chooses deterministic eviction under clock changes.
            Run(connection, transaction, "DELETE FROM UiTermHints WHERE year IN(SELECT year FROM UiTermHints ORDER BY cacheRevision DESC LIMIT -1 OFFSET $maximum);",
                ("$maximum", TermHintCacheSnapshot.MaximumYears));
            Run(connection, transaction, "UPDATE UiTermCacheState SET revision=$revision WHERE id=1;", ("$revision", revision));
            Checkpoint(SqliteStoreWriteStage.UiTermHints);
            var updated = ReadUiTermHints(connection, transaction);
            transaction.Commit();
            return new TermHintWriteResult(TermHintWriteStatus.Applied, updated);
        }, cancellationToken);
    }

    private static TermHintCacheSnapshot ReadUiTermHints(SqliteConnection connection, SqliteTransaction transaction)
    {
        var revision = (long?)Scalar(connection, transaction, "SELECT revision FROM UiTermCacheState WHERE id=1;")
            ?? throw new InvalidDataException("The term hint cache metadata is absent.");
        List<CachedTermYear> rows = [];
        using var query = Command(connection, transaction, "SELECT year,cacheRevision,fetchedUtc,hintsJson FROM UiTermHints ORDER BY year DESC LIMIT $limit;",
            ("$limit", TermHintCacheSnapshot.MaximumYears + 1));
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            if (rows.Count == TermHintCacheSnapshot.MaximumYears) throw new InvalidDataException("The term hint cache exceeds its year bound.");
            rows.Add(new(reader.GetInt32(0), reader.GetInt64(1), StoreWire.Time(reader.GetString(2)), StoreUiWire.ReadTerms(reader.GetString(3))));
        }
        return new(revision, rows);
    }
}
