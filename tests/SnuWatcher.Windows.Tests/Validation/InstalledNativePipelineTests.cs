using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnuWatcher.Windows.Validation;
using Xunit;

namespace SnuWatcher.Windows.Tests.Validation;

public sealed class InstalledNativePipelineTests
{
    [Fact]
    public void SameExecutableWithChangedDesktopLibraryIsRejectedBeforeAnyLaunchCanBeAdmitted()
    {
        using var files = new Payload();
        var manifest = files.Manifest();
        var expected = InstalledNativePipeline.VerifyTrustedBuild(files.Directory, manifest, Digest(manifest));
        Assert.Equal(expected, InstalledNativePipeline.ReadInstalled(files.Directory));
        File.AppendAllText(Path.Combine(files.Directory, "SnuWatcher.Desktop.dll"), "changed pipeline");
        var changed = InstalledNativePipeline.ReadInstalled(files.Directory);
        Assert.Equal(expected.ExecutableSha256, changed.ExecutableSha256);
        Assert.NotEqual(expected.PipelineSha256, changed.PipelineSha256);
        Assert.Throws<InvalidDataException>(() => InstalledNativePipeline.VerifyTrustedBuild(files.Directory, manifest, Digest(manifest)));
    }

    [Theory]
    [InlineData("untrusted_manifest")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("wrong_schema")]
    public void PinnedInventoryRequiresEveryFixedFileExactlyOnceWithItsActualBytesAndHash(string mutation)
    {
        using var files = new Payload();
        var manifest = files.Manifest(mutation);
        Assert.Throws<InvalidDataException>(() => InstalledNativePipeline.VerifyTrustedBuild(files.Directory, manifest,
            mutation == "untrusted_manifest" ? new string('a', 64) : Digest(manifest)));
    }

    [Fact]
    public void DigestUsesTheAgreedFixedOrderAndFinalLfRatherThanCallerEnumerationOrder()
    {
        using var files = new Payload();
        var expected = Digest(Encoding.UTF8.GetBytes(string.Concat(Payload.Names.Select(name => name + "|"
            + Digest(File.ReadAllBytes(Path.Combine(files.Directory, name))) + "\n"))));
        Assert.Equal(expected, InstalledNativePipeline.ReadInstalled(files.Directory).PipelineSha256);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Payload : IDisposable
    {
        internal static readonly string[] Names = ["SnuWatcher.Desktop.exe", "SnuWatcher.Desktop.dll", "SnuWatcher.Windows.dll",
            "SnuWatcher.Infrastructure.dll", "SnuWatcher.Application.dll", "SnuWatcher.Core.dll"];
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "SnuWatcher-pipeline-tests", Guid.NewGuid().ToString("N"));
        internal Payload()
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var name in Names) File.WriteAllText(Path.Combine(Directory, name), name);
        }
        internal byte[] Manifest(string? mutation = null)
        {
            var entries = Names.Select(name => new { path = name, bytes = new FileInfo(Path.Combine(Directory, name)).Length,
                sha256 = Digest(File.ReadAllBytes(Path.Combine(Directory, name))) }).Reverse().ToList();
            if (mutation == "missing") entries.RemoveAt(0);
            if (mutation == "duplicate") entries.Add(entries[0]);
            if (mutation == "size") entries[0] = new { entries[0].path, bytes = 0L, entries[0].sha256 };
            if (mutation == "hash") entries[0] = new { entries[0].path, entries[0].bytes, sha256 = new string('a', 64) };
            return JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = mutation == "wrong_schema" ? 0 : 1,
                source = new { revision = new string('a', 40), dirty = false }, version = "0.1.0-preview.1",
                installerVersion = "0.1.0.0", payload = entries });
        }
        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher-pipeline-tests"));
            if (Path.GetDirectoryName(Path.GetFullPath(Directory)) != parent) throw new InvalidOperationException();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
