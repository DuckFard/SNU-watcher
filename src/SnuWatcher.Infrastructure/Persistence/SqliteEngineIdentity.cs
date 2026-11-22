namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>The native SQLite build actually executing SQL, rather than its managed package version.</summary>
public sealed record SqliteEngineIdentity(string Version, string SourceId)
{
    /// <summary>The explicitly pinned native package, including the WAL-reset corruption fix.</summary>
    public static SqliteEngineIdentity Expected { get; } = new("3.54.1",
        "2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76");

    /// <summary>Fail before application database access if packaging selected a different native engine.</summary>
    public SqliteEngineIdentity RequireSupported() => this == Expected
        ? this
        : throw new NotSupportedException("The SQLite native engine does not match this application's supported build. Repair or reinstall SNU Watcher.");
}
