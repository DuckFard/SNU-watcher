using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnuWatcher.Infrastructure.Persistence;

namespace SnuWatcher.Integration.Tests.Persistence;

/// <summary>Frozen M5 V4 producer; never invokes the current initializer or relabels a newer database.</summary>
internal sealed class StoreHistoryV4Fixture : IDisposable
{
    private readonly StoreUiV3Fixture original = new();
    internal const string ProducerCommit = "7367f5b";
    internal const string PreferencesCommandId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string ValuesJson = """{"version":1,"language":"ko","year":2026,"term":{"year":2026,"selector":"U000200002.U000300002"},"profile":"faster","close":"quit","onboarding":1}""";
    internal static readonly string CommandJson = "{\"version\":1,\"id\":\"" + PreferencesCommandId + "\",\"expected\":0,\"values\":" + ValuesJson + "}";
    internal static readonly string ReceiptJson = "{\"version\":1,\"id\":\"" + PreferencesCommandId
        + "\",\"previous\":0,\"revision\":1,\"saved\":\"" + StoreUiV3Fixture.Time + "\",\"values\":" + ValuesJson + "}";
    internal StoreHistoryV4Fixture()
    {
        try
        {
            original.Alter(FrozenV4Schema + "PRAGMA user_version=4;");
            using var connection = Factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO UiPreferences VALUES(1,1,$at,$values); "
                + "INSERT INTO UiPreferenceWrites VALUES($id,$facts,$receipt); "
                + "UPDATE UiTermCacheState SET revision=1 WHERE id=1; "
                + "INSERT INTO UiTermHints VALUES(2026,1,$at,$hints);";
            command.Parameters.AddWithValue("$at", StoreUiV3Fixture.Time);
            command.Parameters.AddWithValue("$values", ValuesJson);
            command.Parameters.AddWithValue("$id", PreferencesCommandId);
            command.Parameters.AddWithValue("$facts", CommandJson);
            command.Parameters.AddWithValue("$receipt", ReceiptJson);
            command.Parameters.AddWithValue("$hints", """{"version":1,"terms":[{"selector":"U000200002.U000300002","name":"2학기","english":"Fall"}]}""");
            command.ExecuteNonQuery();
        }
        catch { original.Dispose(); throw; }
    }
    internal string DatabasePath => original.DatabasePath;
    internal SqliteConnectionFactory Factory => original.Factory;
    internal long Version => original.Version;
    internal string[] Backups() => original.Backups();
    internal byte[] DatabaseHash() => original.DatabaseHash();
    internal void Alter(string sql) => original.Alter(sql);
    internal void SetHeader(long version, long applicationId) => original.SetHeader(version, applicationId);
    internal string LogicalSnapshot()
    { using var connection = Factory.Open(SqliteOpenMode.ReadOnly); return LogicalSnapshot(connection); }
    internal static string LogicalSnapshot(SqliteConnection connection)
    {
        List<object> tables = [];
        foreach (var table in new[] { "UiPreferences", "UiPreferenceWrites", "UiTermCacheState", "UiTermHints" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            List<object?[]> rows = [];
            while (reader.Read())
            {
                var values = new object[reader.FieldCount]; reader.GetValues(values);
                rows.Add(values.Select(value => value is DBNull ? null : value).ToArray());
            }
            tables.Add(new { table, rows });
        }
        return JsonSerializer.Serialize(new { original = StoreUiV3Fixture.LogicalSnapshot(connection), tables });
    }
    public void Dispose() => original.Dispose();

    // Frozen verbatim from M5 7367f5b: StoreUiSchema.UiSchemaSql.
    private const string FrozenV4Schema = """
        CREATE TABLE UiPreferences(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL CHECK(revision>0),savedUtc TEXT NOT NULL,valuesJson TEXT NOT NULL);
        CREATE TABLE UiPreferenceWrites(commandId TEXT PRIMARY KEY,commandFacts TEXT NOT NULL,receipt TEXT NOT NULL);
        CREATE TABLE UiTermCacheState(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL CHECK(revision>=0));
        INSERT INTO UiTermCacheState VALUES(1,0);
        CREATE TABLE UiTermHints(year INTEGER PRIMARY KEY CHECK(year BETWEEN 2013 AND 2200),cacheRevision INTEGER NOT NULL CHECK(cacheRevision>0),fetchedUtc TEXT NOT NULL,hintsJson TEXT NOT NULL);
        """;
}
