using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreLegacySchemaValidationTests
{
    [Fact]
    public async Task TheSameKnownV2ProducerLayoutWithOppositeLineEndingsCanReopen()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var watch = await fixture.AddAsync(StoreFixture.Key());
        using (var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open())
        {
            string ddl;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT sql FROM sqlite_schema WHERE name='LegacyImports';";
                ddl = (string)read.ExecuteScalar()!;
            }
            Assert.Contains('\n', ddl);
            var opposite = ddl.Contains("\r\n", StringComparison.Ordinal)
                ? ddl.Replace("\r\n", "\n", StringComparison.Ordinal)
                : ddl.Replace("\n", "\r\n", StringComparison.Ordinal);
            using var rewrite = connection.CreateCommand();
            rewrite.CommandText = "DROP TABLE LegacyImports;" + opposite;
            rewrite.ExecuteNonQuery();
        }

        await fixture.ReopenAsync();

        Assert.Equal(watch.Id, Assert.Single(await fixture.Store.ListSubscriptionsAsync(default)).Id);
    }

    [Fact]
    public async Task AReservedPrefixLookalikeDoesNotMakeAForeignDatabaseAppearEmpty()
    {
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Path.Combine(parent, "SnuWatcher-foreign-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "foreign.db");
            var factory = new SqliteConnectionFactory(path);
            using (var connection = factory.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE sqliteXforeign(value TEXT); INSERT INTO sqliteXforeign VALUES('keep');";
                command.ExecuteNonQuery();
            }
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path));

            await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(factory));

            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.Empty(Directory.GetFiles(directory, "*.before-v4-*.sqlite3"));
        }
        finally
        {
            var target = Path.GetFullPath(directory);
            var name = Path.GetFileName(target);
            if (!string.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith("SnuWatcher-foreign-schema-", StringComparison.Ordinal)
                || !Guid.TryParseExact(name["SnuWatcher-foreign-schema-".Length..], "N", out _))
                throw new InvalidOperationException("Refusing cleanup outside the foreign-schema fixture.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
