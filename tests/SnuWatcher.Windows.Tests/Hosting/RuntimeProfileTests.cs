using SnuWatcher.Windows.Hosting;
using Xunit;

namespace SnuWatcher.Windows.Tests.Hosting;

public sealed class RuntimeProfileTests
{
    [Fact]
    public void NewValidationRunHasFixedPathsAndMayBeReopenedByAContender()
    {
        using var fixture = new RuntimeProfileFixture();
        var profile = fixture.Profile;
        var reopened = RuntimeProfile.OpenValidationRun(profile.RunId!.Value);
        Assert.Equal(profile.DatabasePath, reopened.DatabasePath);
        Assert.Equal("watches.sqlite3", Path.GetFileName(profile.DatabasePath));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "SnuWatcher-runtime-validation", profile.RunId.Value.ToString("N")), profile.DirectoryPath);
        Assert.StartsWith("DuckFard.SnuWatcher.Validation.", profile.ApplicationId, StringComparison.Ordinal);
        Assert.False(File.Exists(profile.DatabasePath));
        Assert.Throws<InvalidOperationException>(() => RuntimeProfile.CreateValidationRun(profile.RunId.Value));
    }

    [Fact]
    public void UnknownOrEmptyRunCannotCreateAProfile()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "SnuWatcher-runtime-validation", id.ToString("N"));
        Assert.Throws<InvalidOperationException>(() => RuntimeProfile.OpenValidationRun(id));
        Assert.False(Directory.Exists(path));
        Assert.Throws<ArgumentException>(() => RuntimeProfile.CreateValidationRun(Guid.Empty));
    }

    [Fact]
    public void TamperedOrOversizedManifestIsRejectedBeforeDatabaseCreation()
    {
        using var fixture = new RuntimeProfileFixture();
        File.WriteAllText(fixture.Profile.ManifestPath, "{\"format\":\"snu-runtime-validation-v1\",\"runId\":\"" + Guid.NewGuid().ToString("N") + "\"}");
        Assert.Throws<InvalidOperationException>(() => RuntimeProfile.OpenValidationRun(fixture.Profile.RunId!.Value));
        File.WriteAllText(fixture.Profile.ManifestPath, new string('x', 2048));
        Assert.Throws<InvalidOperationException>(() => RuntimeProfile.OpenValidationRun(fixture.Profile.RunId!.Value));
        Assert.False(File.Exists(fixture.Profile.DatabasePath));
    }

    [Fact]
    public void NonFileSqliteSidecarIsRejectedBeforeAnyStoreWrite()
    {
        using var fixture = new RuntimeProfileFixture();
        Directory.CreateDirectory(fixture.Profile.DatabasePath + "-wal");
        Assert.Throws<InvalidOperationException>(() => RuntimeProfile.OpenValidationRun(fixture.Profile.RunId!.Value));
        Assert.False(File.Exists(fixture.Profile.DatabasePath));
    }

    [Theory]
    [InlineData("--monitor-validation")]
    [InlineData("--monitor-validation=bad")]
    [InlineData("--monitor-validation-wrong")]
    [InlineData("--MONITOR-VALIDATION=bad")]
    [InlineData("--monitor-validation=00000000000000000000000000000000")]
    public void MalformedValidationPrefixesAreHandledPrivately(string argument)
    {
        Assert.Equal(ValidationArgumentDisposition.Invalid, RuntimeProfile.ParseValidationArguments([argument], out _));
    }

    [Fact]
    public void NativeArgumentsStayUnchangedAndValidationCannotBeCombinedWithNativeActions()
    {
        Assert.Equal(ValidationArgumentDisposition.NotRequested, RuntimeProfile.ParseValidationArguments(["--test-notification"], out _));
        var id = Guid.NewGuid();
        Assert.Equal(ValidationArgumentDisposition.Valid, RuntimeProfile.ParseValidationArguments(["--monitor-validation=" + id.ToString("N")], out var parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(ValidationArgumentDisposition.Invalid, RuntimeProfile.ParseValidationArguments(["--monitor-validation=" + id.ToString("N"), "--quit"], out _));
    }
}

internal sealed class RuntimeProfileFixture : IDisposable
{
    internal RuntimeProfileFixture() => Profile = RuntimeProfile.CreateValidationRun(Guid.NewGuid());
    internal RuntimeProfile Profile { get; }
    public void Dispose()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher-runtime-validation"));
        var owned = Path.GetFullPath(Profile.DirectoryPath);
        if (!string.Equals(Path.GetDirectoryName(owned), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(owned), "N", out var id) || id != Profile.RunId)
            throw new InvalidOperationException("Refusing cleanup outside this fixture's private generated run.");
        if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
    }
}
