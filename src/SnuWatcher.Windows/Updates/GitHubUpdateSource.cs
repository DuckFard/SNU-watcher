using System.Net;
using System.Net.Http.Headers;

namespace SnuWatcher.Windows.Updates;

/// <summary>Finds the newest published version of SNU Watcher.</summary>
public interface IUpdateSource
{
    /// <summary>Returns the newest published version, or null if none is published.</summary>
    Task<ReleaseVersion?> GetNewestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Asks GitHub's public API for this repository's release list. The request carries only GitHub's required
/// User-Agent ("SnuWatcher/&lt;version&gt;"): no account, cookies, watches or other data. Redirects are not followed.
/// </summary>
public sealed class GitHubUpdateSource : IUpdateSource, IDisposable
{
    private readonly HttpClient client;
    private readonly ProductInfoHeaderValue userAgent;

    /// <summary>Uses its own anonymous connection, identified only as SnuWatcher/<paramref name="appVersion"/>.</summary>
    public GitHubUpdateSource(ReleaseVersion appVersion)
        : this(appVersion, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        }) { }

    /// <summary>Test seam: the same requests through <paramref name="handler"/>.</summary>
    internal GitHubUpdateSource(ReleaseVersion appVersion, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(appVersion);
        ArgumentNullException.ThrowIfNull(handler);
        userAgent = new ProductInfoHeaderValue("SnuWatcher", appVersion.ToString());
        client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <inheritdoc/>
    /// <exception cref="HttpRequestException">The request failed or GitHub did not answer 200 OK.</exception>
    /// <exception cref="InvalidDataException">The answer is too large or is not a release list.</exception>
    public async Task<ReleaseVersion?> GetNewestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubReleaseFeed.ReleasesApi);
        request.Headers.UserAgent.Add(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("GitHub did not return the release list.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > GitHubReleaseFeed.MaxBytes)
            throw new InvalidDataException("The release list is too large.");
        var buffer = new byte[GitHubReleaseFeed.MaxBytes + 1];
        var length = 0;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            int read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
                length += read;
        }
        return GitHubReleaseFeed.Newest(buffer.AsSpan(0, length));
    }

    /// <inheritdoc/>
    public void Dispose() => client.Dispose();
}
