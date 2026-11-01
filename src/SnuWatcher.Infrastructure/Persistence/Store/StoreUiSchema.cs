namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    // Additive V4 only. Historical V1/V2/V3 definitions remain fixed producer contracts.
    private const string UiSchemaSql = """
        CREATE TABLE UiPreferences(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL CHECK(revision>0),savedUtc TEXT NOT NULL,valuesJson TEXT NOT NULL);
        CREATE TABLE UiPreferenceWrites(commandId TEXT PRIMARY KEY,commandFacts TEXT NOT NULL,receipt TEXT NOT NULL);
        CREATE TABLE UiTermCacheState(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL CHECK(revision>=0));
        INSERT INTO UiTermCacheState VALUES(1,0);
        CREATE TABLE UiTermHints(year INTEGER PRIMARY KEY CHECK(year BETWEEN 2013 AND 2200),cacheRevision INTEGER NOT NULL CHECK(cacheRevision>0),fetchedUtc TEXT NOT NULL,hintsJson TEXT NOT NULL);
        """;
}
