using System.Text;
using System.Text.Json;
using SnuWatcher.Windows.Updates;
using Xunit;

namespace SnuWatcher.Windows.Tests.Updates;

public sealed class GitHubReleaseFeedTests
{
    private static ReleaseVersion? Newest(string json) => GitHubReleaseFeed.Newest(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void TheNewestPublishedPreviewOrReleaseWins()
    {
        var json = """
            [
              {"tag_name":"v0.1.0-preview.2","draft":false,"prerelease":true,"html_url":"https://example.invalid/2"},
              {"tag_name":"v0.1.0-preview.10","draft":false,"prerelease":true},
              {"tag_name":"v0.1.0-preview.3","draft":false,"prerelease":true}
            ]
            """;

        Assert.Equal("0.1.0-preview.10", Newest(json)?.ToString());
    }

    [Fact]
    public void DraftsAndUnrecognizedTagsAreNeverOffered()
    {
        var json = """
            [
              {"tag_name":"v9.0.0","draft":true},
              {"tag_name":"v2.0.0-rc.1","draft":false},
              {"tag_name":"latest","draft":false},
              {"tag_name":42,"draft":false},
              {"draft":false},
              "not an object",
              {"tag_name":"v0.1.0-preview.3","draft":false}
            ]
            """;

        Assert.Equal("0.1.0-preview.3", Newest(json)?.ToString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"tag_name":"v1.0.0","draft":true}]""")]
    public void AListWithNoPublishedVersionHasNoNewest(string json) => Assert.Null(Newest(json));

    [Theory]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    [InlineData("null")]
    public void AResponseThatIsNotAReleaseListIsRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => Newest(json));

    [Fact]
    public void MalformedJsonIsRejected() =>
        Assert.ThrowsAny<JsonException>(() => Newest("[{\"tag_name\":"));

    [Fact]
    public void OversizedResponsesAreRejectedBeforeParsing() =>
        Assert.Throws<InvalidDataException>(() => GitHubReleaseFeed.Newest(new byte[GitHubReleaseFeed.MaxBytes + 1]));

    [Fact]
    public void TheReleasePageIsBuiltFromTheVersionNotTakenFromTheResponse()
    {
        Assert.True(ReleaseVersion.TryParse("0.1.0-preview.4", out var version));

        Assert.Equal("https://github.com/DuckFard/SNU-watcher/releases/tag/v0.1.0-preview.4",
            GitHubReleaseFeed.ReleasePage(version!).AbsoluteUri);
    }
}
