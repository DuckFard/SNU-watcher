using System.Text;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Validation;
using Xunit;

namespace SnuWatcher.Windows.Tests.Validation;

public sealed class NativeDeliveryProfileTests
{
    private static readonly string Hash = new('A', 64);

    [Fact]
    public void FreshRunClaimsExactlyOnceAndRejectsReuseWithoutOverwritingItsClaim()
    {
        var profile = Create();
        try
        {
            var claimed = InstalledNativeDeliveryProfile.Claim(profile.RunId!.Value, Hash, Hash.ToLowerInvariant());
            Assert.Equal(profile.DatabasePath, claimed.Profile.DatabasePath);
            var before = File.ReadAllBytes(Path.Combine(profile.DirectoryPath, "native-delivery-claim.json"));
            Assert.Throws<InvalidDataException>(() => InstalledNativeDeliveryProfile.Claim(profile.RunId.Value, Hash, Hash.ToLowerInvariant()));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(profile.DirectoryPath, "native-delivery-claim.json")));
            Assert.False(File.Exists(profile.DatabasePath));
        }
        finally { Delete(profile); }
    }

    [Theory]
    [InlineData("database")]
    [InlineData("unexpected")]
    [InlineData("fingerprint")]
    [InlineData("pipeline")]
    public void ExistingDataOrForeignExecutableCannotBeClaimedAsAFreshNativeRun(string mode)
    {
        var profile = Create();
        try
        {
            if (mode == "database") File.WriteAllText(profile.DatabasePath, "existing-data");
            if (mode == "unexpected") File.WriteAllText(Path.Combine(profile.DirectoryPath, "unexpected.txt"), "not a fresh run");
            Assert.Throws<InvalidDataException>(() => InstalledNativeDeliveryProfile.Claim(profile.RunId!.Value,
                mode == "fingerprint" ? new string('B', 64) : Hash,
                mode == "pipeline" ? new string('b', 64) : Hash.ToLowerInvariant()));
            Assert.False(File.Exists(Path.Combine(profile.DirectoryPath, "native-delivery-claim.json")));
        }
        finally { Delete(profile); }
    }

    private static RuntimeProfile Create()
    {
        var profile = RuntimeProfile.CreateValidationRun(Guid.NewGuid());
        var marker = "{\"format\":\"snu-installed-native-delivery-v1\",\"runId\":\"" + profile.RunId!.Value.ToString("N")
            + "\",\"case\":\"en-sound\",\"executableSha256\":\"" + Hash + "\",\"pipelineSha256\":\"" + Hash.ToLowerInvariant() + "\"}";
        File.WriteAllBytes(Path.Combine(profile.DirectoryPath, "native-delivery-validation.json"), Encoding.UTF8.GetBytes(marker));
        return profile;
    }
    private static void Delete(RuntimeProfile profile)
    {
        var target = Path.GetFullPath(profile.DirectoryPath);
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher-runtime-validation"));
        if (!string.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) throw new InvalidOperationException("Invalid private cleanup target.");
        Directory.Delete(target, true);
    }
}
