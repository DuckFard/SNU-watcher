using Microsoft.Data.Sqlite;

namespace SnuWatcher.Infrastructure.Legacy;

internal static class LegacySqliteSchema
{
    private static readonly (string Name, string Type, int Required, int PrimaryKey)[] Columns =
    [
        ("storage_key", "TEXT", 0, 1), ("year", "INTEGER", 1, 0), ("semester_code", "TEXT", 1, 0),
        ("subject_code", "TEXT", 1, 0), ("section", "TEXT", 1, 0), ("availability", "TEXT", 1, 0),
        ("episode", "INTEGER", 1, 0), ("alerted_episode", "INTEGER", 1, 0), ("name", "TEXT", 1, 0),
        ("registered", "INTEGER", 0, 0), ("capacity", "INTEGER", 0, 0), ("observed_at", "TEXT", 1, 0),
    ];

    internal static void RequireRecognized(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (Scalar(connection, transaction, "PRAGMA application_id;") is not 0L
            || Scalar(connection, transaction, "PRAGMA user_version;") is not 0L) Reject();
        using (var command = Command(connection, transaction, "SELECT CASE WHEN instr(type,char(0))=0 AND length(CAST(type AS BLOB))<=64 THEN type END, CASE WHEN instr(name,char(0))=0 AND length(CAST(name AS BLOB))<=320 THEN name END FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*' LIMIT 2;"))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.IsDBNull(0) || reader.GetString(0) != "table" || reader.IsDBNull(1)
                || reader.GetString(1) != "course_state" || reader.Read()) Reject();
        }
        using (var command = Command(connection, transaction, "SELECT type,ncol,wr,strict FROM pragma_table_list WHERE schema='main' AND name='course_state';"))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "table" || reader.GetInt32(1) != 12
                || reader.GetInt32(2) != 0 || reader.GetInt32(3) != 0 || reader.Read()) Reject();
        }
        RequireColumns(connection, transaction);
        if (Scalar(connection, transaction, "SELECT count(*) FROM pragma_foreign_key_list('course_state');") is not 0L
            || Scalar(connection, transaction, "SELECT count(*) FROM pragma_index_list('course_state') WHERE origin='pk' AND partial=0 AND \"unique\"=1;") is not 1L
            || Scalar(connection, transaction, "SELECT count(*) FROM pragma_index_list('course_state');") is not 1L) Reject();
    }

    private static void RequireColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, """
            SELECT cid,CASE WHEN instr(name,char(0))=0 AND length(CAST(name AS BLOB))<=320 THEN name END,
                CASE WHEN instr(type,char(0))=0 AND length(CAST(type AS BLOB))<=64 THEN type END,
                "notnull",dflt_value IS NULL,pk,hidden FROM pragma_table_xinfo('course_state') LIMIT 13;
            """);
        using var reader = command.ExecuteReader();
        var index = 0;
        while (reader.Read())
        {
            if (index >= Columns.Length || reader.GetInt32(0) != index || reader.IsDBNull(1) || reader.IsDBNull(2)) Reject();
            var expected = Columns[index++];
            if (reader.GetString(1) != expected.Name || !reader.GetString(2).Equals(expected.Type, StringComparison.OrdinalIgnoreCase)
                || reader.GetInt32(3) != expected.Required || !reader.GetBoolean(4)
                || reader.GetInt32(5) != expected.PrimaryKey || reader.GetInt32(6) != 0) Reject();
        }
        if (index != Columns.Length) Reject();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Reject() => throw new LegacySourceException("legacy_database_schema_unsupported");
}
