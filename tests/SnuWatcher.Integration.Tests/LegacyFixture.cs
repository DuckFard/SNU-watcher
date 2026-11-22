using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SnuWatcher.Infrastructure.Persistence;

namespace SnuWatcher.Integration.Tests.Legacy;

internal sealed class LegacyFixture : IDisposable
{
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher.LegacyReaderTests"));
    private readonly string _directory = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));

    public LegacyFixture() => Directory.CreateDirectory(_directory);
    public string ConfigPath => Path.Combine(_directory, "legacy.toml");
    public string DatabasePath => Path.Combine(_directory, "legacy.sqlite3");
    public string MissingPath => Path.Combine(_directory, "never-created.sqlite3");
    public SqliteConnectionFactory Factory => new(DatabasePath);

    public static string Course(string section = "012", string course = "F32.102L", string label = "통계학실험") =>
        $"[[courses]]\nyear = 2026\nsemester_code = \"U000200002\"\ncourse_code = \"{course}\"\nsection = \"{section}\"\nlabel = \"{label}\"\n";

    public void WriteConfig(string value) => File.WriteAllText(ConfigPath, value, new UTF8Encoding(false, true));
    public static byte[] Hash(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(file);
    }

    public SqliteConnection CreateDatabase(bool wal = false)
    {
        var connection = Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE course_state (
                storage_key TEXT PRIMARY KEY,
                year INTEGER NOT NULL,
                semester_code TEXT NOT NULL,
                subject_code TEXT NOT NULL,
                section TEXT NOT NULL,
                availability TEXT NOT NULL,
                episode INTEGER NOT NULL,
                alerted_episode INTEGER NOT NULL,
                name TEXT NOT NULL,
                registered INTEGER,
                capacity INTEGER,
                observed_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        if (wal) { command.CommandText = "PRAGMA journal_mode=WAL;"; command.ExecuteScalar(); }
        return connection;
    }

    public static void Insert(SqliteConnection connection, string section = "012", int registered = 119,
        string? storageKey = null, string observed = "2026-11-01T10:30:00+09:00", string name = "통계학실험")
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO course_state(storage_key,year,semester_code,subject_code,section,availability,episode,
                alerted_episode,name,registered,capacity,observed_at)
            VALUES($key,2026,'U000200002','F32.102L',$section,'available',99,99,$name,$registered,120,$observed);
            """;
        command.Parameters.AddWithValue("$key", storageKey ?? $"2026:U000200002:F32.102L:{section}");
        command.Parameters.AddWithValue("$section", section);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$registered", registered);
        command.Parameters.AddWithValue("$observed", observed);
        command.ExecuteNonQuery();
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(_directory);
        if (!string.Equals(Path.GetDirectoryName(target), FixtureRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Refusing cleanup outside this fixture's unique temporary directory.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
