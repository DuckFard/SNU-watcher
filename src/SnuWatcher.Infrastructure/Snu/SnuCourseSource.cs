using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace SnuWatcher.Infrastructure.Snu;

/// <summary>Anonymous, bounded SNU website adapter. It never logs in or submits registration.</summary>
public sealed class SnuCourseSource : IDisposable
{
    private const string Base = "https://sugang.snu.ac.kr";
    private readonly HttpClient http;
    private readonly SnuSourceOptions limits;
    private readonly SemaphoreSlim requests = new(1, 1);
    private long lastRequest;

    public SnuCourseSource(HttpClient httpClient, SnuSourceOptions? options = null)
    {
        http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        limits = options ?? SnuSourceOptions.Default;
        if (limits.MaxPages is < 1 or > 100 || limits.MaxResponseBytes is < 1024 or > 10_000_000 || limits.RequestSpacing < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (limits.RequestTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    /// <summary>Creates a transport that never follows redirects or shares authenticated browser cookies.</summary>
    public static SnuCourseSource Create() => new(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer(),
        UseCookies = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromSeconds(20) });

    /// <summary>Loads source term selectors and currently available department options, preserving raw source codes.</summary>
    public async Task<SourceCatalog> GetCatalogAsync(int year, string? termSelector = null, string? upDepartment = null,
        string? department = null, CancellationToken cancellationToken = default)
    {
        ValidateYear(year);
        ValidateCode(termSelector);
        ValidateCode(upDepartment);
        ValidateCode(department);
        var text = await PostAsync("cc100ajax.action", new Dictionary<string, string>
        {
            ["srchOpenSchyy"] = year.ToString(CultureInfo.InvariantCulture),
            ["srchOpenShtm"] = termSelector ?? "", ["openUpDeptCd"] = upDepartment ?? "", ["openDeptCd"] = department ?? ""
        }, json: true, cancellationToken);
        return SnuJsonParser.Catalog(text, year);
    }

    /// <summary>Loads bounded pages. Later-page failures return partial coverage, never a false empty/full result.</summary>
    public async Task<SourceSearchResult> SearchAsync(SourceSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Term);
        ValidateYear(query.Term.Year);
        if (!SourceSectionIdentity.Valid(query.Term.Selector)) throw new ArgumentException("A source term selector is required.", nameof(query));
        ValidateCode(query.CourseCode);
        ValidateCode(query.UpDepartment);
        ValidateCode(query.Department);
        ValidateCode(query.Major);
        ValidateText(query.Name);
        ValidateText(query.Instructor);
        var pending = new SortedSet<int> { 1 };
        var fetched = new HashSet<int>();
        var sections = new Dictionary<SourceSectionIdentity, SourceSection>();
        var failures = new List<SourceFailure>();
        while (pending.Count > 0)
        {
            var page = pending.Min;
            pending.Remove(page);
            try
            {
                var html = await PostAsync("cc100InterfaceSrch.action", SearchForm(query, page), json: false, cancellationToken);
                var parsed = SnuSearchParser.Parse(html, query.Term);
                fetched.Add(page);
                foreach (var section in parsed.Sections) sections[section.Identity] = section;
                if (parsed.Pages.Any(number => number < 1 || number > limits.MaxPages))
                {
                    failures.Add(new(SourceFailureKind.LimitReached, "Search exceeds the configured page bound; refine the filters."));
                    break;
                }
                foreach (var number in parsed.Pages.Where(number => !fetched.Contains(number))) pending.Add(number);
            }
            catch (SnuSourceException error) when (fetched.Count > 0)
            {
                failures.Add(new(error.Kind, error.Message, error.RetryAfter));
                break;
            }
        }
        return new(Array.AsReadOnly(sections.Values.ToArray()), failures.Count == 0,
            Array.AsReadOnly(failures.ToArray()), fetched.Count);
    }

    /// <summary>Returns public details only when every raw identity field agrees with the requested section.</summary>
    public async Task<SourceDetails> GetDetailsAsync(SourceSectionIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var text = await PostAsync("cc101ajax.action", new Dictionary<string, string>
        {
            ["workType"] = "S", ["openSchyy"] = identity.Year.ToString(CultureInfo.InvariantCulture),
            ["openShtmFg"] = identity.SemesterCode, ["openDetaShtmFg"] = identity.DetailSemesterCode,
            ["sbjtCd"] = identity.CourseCode, ["ltNo"] = identity.Section, ["sbjtSubhCd"] = identity.SubsubjectCode
        }, json: true, cancellationToken);
        return SnuJsonParser.Details(text, identity);
    }

    private static Dictionary<string, string> SearchForm(SourceSearchQuery query, int page) => new()
    {
        ["workType"] = "S", ["pageNo"] = page.ToString(CultureInfo.InvariantCulture),
        ["srchCurrPage"] = page.ToString(CultureInfo.InvariantCulture), ["srchPageSize"] = "10",
        ["srchOpenSchyy"] = query.Term.Year.ToString(CultureInfo.InvariantCulture), ["srchOpenShtm"] = query.Term.Selector,
        ["srchSbjtCd"] = query.CourseCode ?? "", ["srchSbjtNm"] = query.Name ?? "", ["srchProfNm"] = query.Instructor ?? "",
        ["srchOpenUpDeptCd"] = query.UpDepartment ?? "", ["srchOpenDeptCd"] = query.Department ?? "", ["srchOpenMjCd"] = query.Major ?? ""
    };

    private async Task<string> PostAsync(string action, Dictionary<string, string> form, bool json, CancellationToken cancellationToken)
    {
        await requests.WaitAsync(cancellationToken);
        try
        {
            if (lastRequest != 0)
            {
                var delay = limits.RequestSpacing - Stopwatch.GetElapsedTime(lastRequest);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            }
            lastRequest = Stopwatch.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(limits.RequestTimeout ?? TimeSpan.FromSeconds(20));
            var requestToken = deadline.Token;
            var uri = new Uri($"{Base}/sugang/cc/{action}");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
            request.Headers.UserAgent.ParseAdd("SNU-Watcher/0.1 (anonymous public course monitor)");
            request.Headers.Referrer = new Uri(action == "cc101ajax.action" ? $"{Base}/sugang/cc/cc100InterfaceSrch.action" : $"{Base}/sugang/co/co100.action");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            ValidateResponse(response, uri, json);
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, requestToken);
                if (count == 0) break;
                if (buffer.Length + count > limits.MaxResponseBytes) throw TooLarge();
                buffer.Write(bytes, 0, count);
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
        catch (HttpRequestException) { throw new SnuSourceException(SourceFailureKind.Unavailable, "The public SNU data source could not be reached."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SnuSourceException(SourceFailureKind.Unavailable, "The public SNU request timed out."); }
        finally { requests.Release(); }
    }

    private void ValidateResponse(HttpResponseMessage response, Uri expected, bool json)
    {
        if (response.RequestMessage?.RequestUri != expected || (int)response.StatusCode is >= 300 and < 400)
            throw new SnuSourceException(SourceFailureKind.AccessRestricted, "SNU redirected this request; automatic access is paused.");
        if ((int)response.StatusCode == 429)
        {
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
            throw new SnuSourceException(SourceFailureKind.RateLimited, "SNU is limiting requests.", delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        }
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new SnuSourceException(SourceFailureKind.AccessRestricted, "SNU public access is restricted.");
        if (!response.IsSuccessStatusCode) throw new SnuSourceException(SourceFailureKind.Unavailable, "SNU public search is unavailable.");
        if (response.Content.Headers.ContentLength > limits.MaxResponseBytes) throw TooLarge();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        // The observed details action emits JSON with text/html. Only that exact action
        // accepts this legacy media type; the strict JSON/identity parser still rejects HTML gates.
        var legacyDetailsJson = expected.AbsolutePath == "/sugang/cc/cc101ajax.action" && mediaType == "text/html";
        if (json ? mediaType is not ("application/json" or "text/json") && !legacyDetailsJson : mediaType != "text/html")
            throw new SnuSourceException(SourceFailureKind.ParseChanged, "SNU returned an unexpected content type.");
    }

    private static SnuSourceException TooLarge() => new(SourceFailureKind.ResponseTooLarge, "SNU returned a response exceeding the configured size bound.");
    private static void ValidateYear(int year) { if (year is < 2013 or > 2200) throw new ArgumentOutOfRangeException(nameof(year)); }
    private static void ValidateCode(string? value) { if (!string.IsNullOrEmpty(value) && !SourceSectionIdentity.Valid(value)) throw new ArgumentException("Invalid source filter identifier."); }
    private static void ValidateText(string? value) { if (value?.Length > 200 || value?.Any(char.IsControl) == true) throw new ArgumentException("Search text must be at most 200 printable characters."); }

    public void Dispose()
    {
        http.Dispose();
        requests.Dispose();
    }
}
