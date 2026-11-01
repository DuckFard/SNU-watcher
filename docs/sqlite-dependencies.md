# SQLite dependency and runtime identity

Verified on 2026-11-01 with the pinned .NET SDK. This records the persistence dependencies; it does not claim completion of the watch engine or qualification of a new installed desktop build.

| Component | Pin | License |
| --- | --- | --- |
| Microsoft.Data.Sqlite.Core | 10.0.14 | [MIT notice](third-party/Microsoft.Data.Sqlite-LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 and its core/config/provider | 3.0.5 | [Apache-2.0 license](third-party/SQLitePCLRaw-LICENSE.txt) |
| SQLite native package | 3.54.1 | [Public domain](https://www.nuget.org/packages/SQLite/3.54.1/License) |

Microsoft's [custom-version guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions) supports using `.Core` with an explicitly selected native provider. The [bundle dependency graph](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/3.0.5) otherwise permits native SQLite 3.53.4; the explicit direct pin and lock files select 3.54.1.

The actual x64 probe and a self-contained publish both execute `sqlite_version()` as `3.54.1` and `sqlite_source_id()` as:

```text
2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76
```

This matches the [official release](https://www.sqlite.org/releaselog/3_54_1.html) and includes the [WAL-reset fix](https://www.sqlite.org/wal.html). The published probe loads its own `e_sqlite3.DLL`, SHA-256 `922AB7FF7E47EF2694CA5F62ADBEFCC44C637BF5D082488C286816ED5D06363F`. These probe results do not replace tests of the production connection factory or a later installed app.

The production factory verifies this native version and source ID in memory before opening the application file. Connections use private cache, foreign keys, a five-second timeout and no pooling. The factory does not change journal mode or schema. The store must first recognize its schema before enabling WAL. Database work runs as whole synchronous operations on a bounded background lane because [the driver's asynchronous methods execute synchronously](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).

`tests/scripts/Test-PersistencePublish.ps1` additionally verifies the actual production factory/store/preparer/owner helper in a self-contained win-x64 executable. It passes with app-local native loading, one persisted event across reopen/replay, a zero child exit and no stderr. This published-code test is separate from qualification of a later installed desktop build.

The notice files preserve the [driver's exact tagged license](https://raw.githubusercontent.com/dotnet/efcore/v10.0.14/LICENSE.txt) and [SQLitePCLRaw's exact tagged license](https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v3.0.5/LICENSE.TXT). A desktop distribution that includes these libraries must carry those licenses and applicable notices. The MIT license for SNU Watcher's own code does not replace dependency licenses; the full distribution notice review remains part of packaging acceptance.
