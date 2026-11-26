using System.Net;
using System.Text;
using Microsoft.Win32;
using SnuWatcher.Windows.Updates;
using Xunit;

namespace SnuWatcher.Windows.Tests.Updates;

public sealed class GitHubUpdateSourceTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text, out var version) ? version! : throw new ArgumentException(text);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Request = request; return Task.FromResult(answer(request)); }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ItAsksOnlyForThisRepositorysReleaseListAndIdentifiesOnlyTheApp()
    {
        var handler = new Handler(_ => Json("""[{"tag_name":"v0.1.0-preview.4","draft":false}]"""));
        using var source = new GitHubUpdateSource(V("0.1.0-preview.3"), handler);

        var newest = await source.GetNewestAsync(CancellationToken.None);

        Assert.Equal("0.1.0-preview.4", newest?.ToString());
        var request = handler.Request!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(GitHubReleaseFeed.ReleasesApi, request.RequestUri);
        Assert.Equal("SnuWatcher/0.1.0-preview.3", request.Headers.UserAgent.ToString());
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Null(request.Headers.Authorization);
        Assert.Null(request.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    public async Task AnythingButOkFails(HttpStatusCode status)
    {
        using var source = new GitHubUpdateSource(V("0.1.0-preview.3"), new Handler(_ => Json("[]", status)));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetNewestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnOversizedAnswerIsRejected()
    {
        var huge = "[" + new string(' ', GitHubReleaseFeed.MaxBytes) + "]";
        using var source = new GitHubUpdateSource(V("0.1.0-preview.3"), new Handler(_ => Json(huge)));

        await Assert.ThrowsAsync<InvalidDataException>(() => source.GetNewestAsync(CancellationToken.None));
    }
}

/// <summary>Runs the real registry logic under a throwaway per-test key; the app's own key is never touched.</summary>
public sealed class RegistryUpdateCheckStoreTests : IDisposable
{
    private const string Parent = @"Software\SnuWatcherTests";
    private readonly string path = Parent + @"\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(Parent))
            if (parent is null || parent.SubKeyCount > 0 || parent.ValueCount > 0) return;
        Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
    }

    private RegistryUpdateCheckStore Create() => new(Registry.CurrentUser, path);

    [Fact]
    public void ItStartsEnabledWithNoCheckRecorded()
    {
        var store = Create();
        Assert.True(store.ReadEnabled());
        Assert.Null(store.ReadLastChecked());
        Assert.Null(store.ReadNewestSeen());
    }

    [Fact]
    public void TheSwitchAndTheLastCheckAreKept()
    {
        var at = new DateTimeOffset(2026, 11, 26, 12, 30, 15, TimeSpan.FromHours(9));
        Create().WriteEnabled(false);
        Create().WriteChecked(at, "0.1.0-preview.4");

        var store = Create();
        Assert.False(store.ReadEnabled());
        Assert.Equal(at, store.ReadLastChecked());
        Assert.Equal(TimeSpan.Zero, store.ReadLastChecked()!.Value.Offset);
        Assert.Equal("0.1.0-preview.4", store.ReadNewestSeen());

        store.WriteEnabled(true);
        store.WriteChecked(at.AddDays(1), null);
        Assert.True(store.ReadEnabled());
        Assert.Null(store.ReadNewestSeen());
    }

    [Fact]
    public void AnUnreadableTimestampCountsAsNoCheck()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(path)) key.SetValue("UpdateCheckedUtc", "yesterday");
        Assert.Null(Create().ReadLastChecked());
    }

    [Fact]
    public void TheAppUsesItsOwnKeyAndTheInstallerRemovesItOnUninstall()
    {
        Assert.Equal(@"Software\DuckFard\SnuWatcher", RegistryUpdateCheckStore.KeyPath);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "installer", "SnuWatcher.iss"))) directory = directory.Parent;
        var script = File.ReadAllText(Path.Combine(directory!.FullName, "installer", "SnuWatcher.iss"));
        Assert.Contains("Root: HKCU; Subkey: \"" + RegistryUpdateCheckStore.KeyPath + "\"; Flags: uninsdeletekey", script);
    }
}
