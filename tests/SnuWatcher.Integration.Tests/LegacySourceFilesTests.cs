using System.Text;
using SnuWatcher.Infrastructure.Legacy;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class LegacySourceFilesTests : IDisposable
{
    private const string FixturePrefix = "SnuWatcher-legacy-source-";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), FixturePrefix + Guid.NewGuid().ToString("N"));

    public LegacySourceFilesTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ReadsStrictUtf8WithOptionalBomAndCanonicalizesTheSelectedPath()
    {
        var path = Path.Combine(_directory, "서울 watcher.toml");
        const string content = "title = '통계학'\n";
        File.WriteAllText(path, content, new UTF8Encoding(true));
        Directory.CreateDirectory(Path.Combine(_directory, "nested"));
        var selected = Path.Combine(_directory, "nested", "..", Path.GetFileName(path));

        Assert.Equal(Path.GetFullPath(path), LegacySourceFiles.ValidateSelectedFile(selected));
        Assert.Equal(content, LegacySourceFiles.ReadConfiguration(selected));
        File.WriteAllText(path, content, new UTF8Encoding(false));
        Assert.Equal(content, LegacySourceFiles.ReadConfiguration(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(262144)]
    public void ConfigurationAllowsItsExactByteBoundary(int bytes)
    {
        var path = Path.Combine(_directory, "bounded.toml");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', bytes).ToArray());
        Assert.Equal(bytes, LegacySourceFiles.ReadConfiguration(path).Length);
    }

    [Fact]
    public void ConfigurationRejectsMoreThan256KiBWithoutReturningPartialContent()
    {
        var path = Path.Combine(_directory, "oversize.toml");
        using (var file = File.Create(path)) file.SetLength(262145);
        AssertCode("legacy_source_too_large", () => LegacySourceFiles.ReadConfiguration(path));
    }

    [Theory]
    [InlineData(new byte[] { 0xC3, 0x28 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 })]
    [InlineData(new byte[] { 0xF0, 0x9F, 0x92 })]
    public void ConfigurationRejectsInvalidUtf8(byte[] bytes)
    {
        var path = Path.Combine(_directory, "encoding.toml");
        File.WriteAllBytes(path, bytes);
        AssertCode("legacy_config_encoding_invalid", () => LegacySourceFiles.ReadConfiguration(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.toml")]
    [InlineData("file:///C:/watcher.toml")]
    [InlineData("\\\\server\\share\\watcher.toml")]
    [InlineData("//server/share/watcher.toml")]
    [InlineData("\\\\?\\C:\\watcher.toml")]
    [InlineData("\\\\.\\NUL")]
    public void RejectsNonLocalOrNonAbsoluteInputBeforeAccess(string? path) =>
        AssertCode("legacy_source_path_invalid", () => LegacySourceFiles.ValidateSelectedFile(path!));

    [Fact]
    public void RejectsAlternateDataStreamsAndDeviceNames()
    {
        AssertCode("legacy_source_path_invalid", () => LegacySourceFiles.ValidateSelectedFile(Path.Combine(_directory, "watcher.toml:secret")));
        foreach (var name in new[] { "NUL", "CON.toml", "COM1", "LPT9.txt" })
            AssertCode("legacy_source_path_invalid", () => LegacySourceFiles.ValidateSelectedFile(Path.Combine(_directory, name)));
    }

    [Fact]
    public void MissingAndDirectorySourcesHaveBoundedErrorsAndDoNotCreateAnything()
    {
        var missing = Path.Combine(_directory, "absent", "private-source-marker.toml");
        AssertCode("legacy_source_missing", () => LegacySourceFiles.ReadConfiguration(missing));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        AssertCode("legacy_source_not_regular", () => LegacySourceFiles.ValidateSelectedFile(_directory));
    }

    [Fact]
    public void CancellationPreventsOpeningConfiguration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LegacySourceFiles.ReadConfiguration("does-not-exist.toml", cancellation.Token));
    }

    [Fact]
    public void ConfigurationRefusesAnExistingWriterInsteadOfReadingChangingData()
    {
        var path = Path.Combine(_directory, "writing.toml");
        File.WriteAllText(path, "watch = 'fixture'", new UTF8Encoding(false));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        AssertCode("legacy_source_unavailable", () => LegacySourceFiles.ReadConfiguration(path));
    }

    [Fact]
    public void ExclusiveSourceAccessErrorsNeverExposeTheSelectedPath()
    {
        var path = Path.Combine(_directory, "private-source-marker.toml");
        File.WriteAllText(path, "watch = 'fixture'");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AssertCode("legacy_source_unavailable", () => LegacySourceFiles.ValidateSelectedFile(path));
    }

    [Fact]
    public void DatabaseValidationKeepsAnExternalWriterOpenAndDoesNotAlterTheSource()
    {
        var path = Database("live.sqlite3");
        var before = File.ReadAllBytes(path);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(Path.GetFullPath(path), LegacySourceFiles.ValidateDatabase(path));
        Assert.True(writer.CanWrite);
        using var observed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        observed.CopyTo(bytes);
        Assert.Equal(before, bytes.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    public void DatabaseRejectsMissingOrIncorrectHeader(int length)
    {
        var path = Path.Combine(_directory, "not-database.sqlite3");
        File.WriteAllBytes(path, new byte[length]);
        AssertCode("legacy_database_header_invalid", () => LegacySourceFiles.ValidateDatabase(path));
    }

    [Fact]
    public void DatabaseBudgetIncludesWalButNotShmAndAllowsTheExactBoundary()
    {
        var path = Database("budget.sqlite3", 25 * 1024 * 1024 - 32);
        using (var wal = File.Create(path + "-wal")) wal.SetLength(32);
        using (var shm = File.Create(path + "-shm")) shm.SetLength(4096);
        Assert.Equal(Path.GetFullPath(path), LegacySourceFiles.ValidateDatabase(path));
        using (var wal = new FileStream(path + "-wal", FileMode.Open, FileAccess.Write)) wal.SetLength(33);
        AssertCode("legacy_source_too_large", () => LegacySourceFiles.ValidateDatabase(path));
    }

    [Fact]
    public void DatabaseRejectsAnOversizedFileAndNonRegularSidecars()
    {
        var path = Database("oversize.sqlite3", 25 * 1024 * 1024 + 1);
        AssertCode("legacy_source_too_large", () => LegacySourceFiles.ValidateDatabase(path));
        path = Database("directory-sidecar.sqlite3");
        Directory.CreateDirectory(path + "-wal");
        AssertCode("legacy_source_not_regular", () => LegacySourceFiles.ValidateDatabase(path));
        Directory.Delete(path + "-wal");
        Directory.CreateDirectory(path + "-shm");
        AssertCode("legacy_source_not_regular", () => LegacySourceFiles.ValidateDatabase(path));
    }

    [Fact]
    public void RejectsAnAncestorJunctionRatherThanFollowingIt()
    {
        var target = Path.Combine(_directory, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "watcher.toml"), "watch = 'fixture'");
        var link = Path.Combine(_directory, "junction");
        CreateDirectoryLink(link, target);
        AssertCode("legacy_source_not_regular", () => LegacySourceFiles.ReadConfiguration(Path.Combine(link, "watcher.toml")));
    }

    [Fact]
    public void RejectsAReparseSidecarRatherThanFollowingIt()
    {
        var path = Database("linked-sidecar.sqlite3");
        var target = Path.Combine(_directory, "sidecar-target");
        Directory.CreateDirectory(target);
        CreateDirectoryLink(path + "-wal", target);
        AssertCode("legacy_source_not_regular", () => LegacySourceFiles.ValidateDatabase(path));
    }

    private string Database(string name, long size = 16)
    {
        var path = Path.Combine(_directory, name);
        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes("SQLite format 3\0"));
        file.SetLength(size);
        return path;
    }

    private static void AssertCode(string expected, Action operation)
    {
        var error = Assert.Throws<LegacySourceException>(operation);
        Assert.Equal(expected, error.Code);
        Assert.Equal(expected, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-source-marker", error.ToString(), StringComparison.Ordinal);
    }

    private void CreateDirectoryLink(string link, string target) =>
        NativeJunctionFixture.Create(_directory, link, target);

    public void Dispose()
    {
        var canonical = Path.GetFullPath(_directory);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var name = Path.GetFileName(canonical);
        if (!canonical.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !name.StartsWith(FixturePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[FixturePrefix.Length..], "N", out _) || (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing cleanup outside the owned legacy fixture.");
        // Remove only the owned links themselves before recursive cleanup; never traverse their targets.
        foreach (var child in Directory.EnumerateDirectories(canonical).ToArray())
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) continue;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(child)), canonical, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the owned legacy fixture.");
            Directory.Delete(child, recursive: false);
        }
        Directory.Delete(canonical, recursive: true);
    }
}
