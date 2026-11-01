using System.Text;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Validation;
using Xunit;

namespace SnuWatcher.Windows.Tests.Validation;

public sealed class InstalledNativeDeliveryProtocolTests
{
    private static readonly Guid RunId = Guid.ParseExact("aabbccddaabbccddaabbccddaabbccdd", "N");
    private static readonly string Hash = new('A', 64);

    [Theory]
    [InlineData("en-sound", InstalledNativeDeliveryCase.EnglishSound)]
    [InlineData("ko-muted", InstalledNativeDeliveryCase.KoreanMuted)]
    [InlineData("permission-disabled", InstalledNativeDeliveryCase.PermissionDisabled)]
    public void CanonicalOptInBindsExactlyOneRunCaseAndExecutable(string name, InstalledNativeDeliveryCase expected)
    {
        var args = new[] { "--native-delivery-validation=" + RunId.ToString("N") };
        Assert.Equal(ValidationArgumentDisposition.Valid, InstalledNativeDeliveryProtocol.ParseArguments(args, out var actual));
        Assert.Equal(RunId, actual);
        var marker = Encoding.UTF8.GetBytes("{\"format\":\"snu-installed-native-delivery-v1\",\"runId\":\"" + RunId.ToString("N")
            + "\",\"case\":\"" + name + "\",\"executableSha256\":\"" + Hash + "\",\"pipelineSha256\":\"" + Hash.ToLowerInvariant() + "\"}");
        var run = InstalledNativeDeliveryProtocol.ReadManifest(marker, actual);
        Assert.Equal(expected, run.Case);
        Assert.Equal(Hash, run.ExecutableSha256);
        Assert.Equal(Hash.ToLowerInvariant(), run.PipelineSha256);
        Assert.Equal(RunId, run.RunId);
    }

    [Theory]
    [InlineData("--native-delivery-validation")]
    [InlineData("--native-delivery-validation-wrong")]
    [InlineData("--NATIVE-delivery-validation=aabbccddaabbccddaabbccddaabbccdd")]
    [InlineData("--native-delivery-validation=AABBCCDDAABBCCDDAABBCCDDAABBCCDD")]
    [InlineData("--native-delivery-validation=00000000000000000000000000000000")]
    [InlineData("--native-delivery-validation=aabbccdd-aabb-ccdd-aabb-ccddaabbccdd")]
    public void MalformedPrefixCannotFallThroughToNormalNativeStartup(string arg) =>
        Assert.Equal(ValidationArgumentDisposition.Invalid, InstalledNativeDeliveryProtocol.ParseArguments([arg], out _));

    [Fact]
    public void MixedNativeOptInRejectsBeforeAnyOtherRouteAndExistingModesStayUnchanged()
    {
        var valid = "--native-delivery-validation=" + RunId.ToString("N");
        Assert.Equal(ValidationArgumentDisposition.Invalid, InstalledNativeDeliveryProtocol.ParseArguments([valid, "--test-notification"], out _));
        Assert.Equal(ValidationArgumentDisposition.Invalid, InstalledNativeDeliveryProtocol.ParseArguments([valid, valid], out _));
        Assert.Equal(ValidationArgumentDisposition.NotRequested, InstalledNativeDeliveryProtocol.ParseArguments(["--native-validation"], out _));
        Assert.Equal(ValidationArgumentDisposition.NotRequested, InstalledNativeDeliveryProtocol.ParseArguments([], out _));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("foreign_run")]
    [InlineData("lower_hash")]
    [InlineData("extra")]
    [InlineData("large")]
    [InlineData("legacy_exe_only")]
    [InlineData("upper_pipeline")]
    public void ManifestDoesNotAuthorizeAnUnknownCaseOrUnboundRunOrExecutable(string mode)
    {
        var json = "{\"format\":\"snu-installed-native-delivery-v1\",\"runId\":\"" + RunId.ToString("N")
            + "\",\"case\":\"en-sound\",\"executableSha256\":\"" + Hash + "\",\"pipelineSha256\":\"" + Hash.ToLowerInvariant() + "\"}";
        json = mode switch
        {
            "unknown" => json.Replace("en-sound", "anything"),
            "duplicate" => json.Replace("\"case\":", "\"case\":\"ko-muted\",\"case\":"),
            "foreign_run" => json.Replace(RunId.ToString("N"), Guid.NewGuid().ToString("N")),
            "lower_hash" => json.Replace(Hash, Hash.ToLowerInvariant()),
            "extra" => json[..^1] + ",\"path\":\"C:/other\"}",
            "legacy_exe_only" => json.Replace(",\"pipelineSha256\":\"" + Hash.ToLowerInvariant() + "\"", ""),
            "upper_pipeline" => json.Replace(Hash.ToLowerInvariant(), Hash),
            _ => new string(' ', 1025),
        };
        Assert.Throws<InvalidDataException>(() => InstalledNativeDeliveryProtocol.ReadManifest(Encoding.UTF8.GetBytes(json), RunId));
    }
}
