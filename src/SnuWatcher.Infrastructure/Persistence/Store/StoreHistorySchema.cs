namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    // V5 adds only the proven global keyset order; historical V1–V4 producer DDL stays unchanged.
    private const string HistorySchemaSql = """
        CREATE INDEX AlertHistoryOrder ON Events(triggerOrdinal DESC,id COLLATE BINARY DESC);
        """;
}
