using System.Globalization;
using System.Net;
using System.Text;
using SnuWatcher.Application.Sources;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Snu.Policy;
using SnuWatcher.Infrastructure.Snu.RequestQueue;

namespace SnuWatcher.Infrastructure.Snu;

/// <summary>Anonymous, bounded SNU website adapter. It never logs in or submits registration.</summary>
public sealed partial class SnuCourseSource : IDisposable, IAsyncDisposable
{
    private const string Base = "https://sugang.snu.ac.kr";
    private readonly HttpClient http;
    private readonly SnuSourceOptions limits;
    private readonly TimeProvider timeProvider;
    private readonly SnuRequestDispatcher requests;

    /// <summary>Unhosted diagnostics compatibility only. Production hosting must use CreateAsync with its policy store.</summary>
    public SnuCourseSource(HttpClient httpClient, SnuSourceOptions? options = null, TimeProvider? timeProvider = null)
        : this(httpClient, options, timeProvider, null, false) { }

    private SnuCourseSource(HttpClient httpClient, SnuSourceOptions? options, TimeProvider? timeProvider,
        SnuRequestDispatcher? dispatcher, bool testing, ISourcePolicyStore? policyStore = null, Func<Task>? afterScopeReleased = null)
    {
        http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        limits = options ?? SnuSourceOptions.Default;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (limits.MaxPages is < 1 or > 100 || limits.MaxResponseBytes is < 1024 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (limits.RequestTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)))
            throw new ArgumentOutOfRangeException(nameof(options));
        var queueOptions = testing ? SourceQueueOptions.ForTesting(limits.RequestSpacing) : new SourceQueueOptions(limits.RequestSpacing);
        requests = dispatcher ?? new SnuRequestDispatcher(this.timeProvider, queueOptions, applyEscapingFailures: policyStore is null);
        storedPolicy = policyStore is null ? null : new(policyStore, requests, this.timeProvider, afterScopeReleased);
    }

    /// <summary>Offline friend seam only. Ownership of both the client and optional dispatcher transfers to this source.</summary>
    internal static SnuCourseSource CreateForTesting(HttpClient httpClient, SnuSourceOptions? options = null,
        TimeProvider? timeProvider = null, SnuRequestDispatcher? dispatcher = null) => new(httpClient, options, timeProvider, dispatcher, true);

    /// <summary>Unhosted diagnostics compatibility only. Production hosting must use the store-backed CreateAsync factory.</summary>
    public static SnuCourseSource Create() => new(AnonymousClient());

    private static HttpClient AnonymousClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer(),
        UseCookies = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Loads source term selectors and currently available department options, preserving raw source codes.</summary>
    public Task<SourceCatalog> GetCatalogAsync(int year, string? termSelector = null, string? upDepartment = null,
        string? department = null, CancellationToken cancellationToken = default) =>
        GetCatalogAsync(year, SourceRequestPurpose.Interactive, termSelector, upDepartment, department, cancellationToken);

    public async Task<SourceCatalog> GetCatalogAsync(int year, SourceRequestPurpose purpose, string? termSelector = null,
        string? upDepartment = null, string? department = null, CancellationToken cancellationToken = default)
    {
        ValidateYear(year);
        ValidateCode(termSelector);
        ValidateCode(upDepartment);
        ValidateCode(department);
        var text = await PostAsync("cc100ajax.action", new Dictionary<string, string>
        {
            ["srchOpenSchyy"] = year.ToString(CultureInfo.InvariantCulture),
            ["srchOpenShtm"] = termSelector ?? "", ["openUpDeptCd"] = upDepartment ?? "", ["openDeptCd"] = department ?? ""
        }, json: true, purpose, text => SnuJsonParser.Catalog(text, year), cancellationToken).ConfigureAwait(false);
        return text.Data;
    }

    /// <summary>Loads bounded pages. Later-page failures return partial coverage, never a false empty/full result.</summary>
    public Task<SourceSearchResult> SearchAsync(SourceSearchQuery query, CancellationToken cancellationToken = default) =>
        SearchAsync(query, SourceRequestPurpose.Interactive, cancellationToken);

    public async Task<SourceSearchResult> SearchAsync(SourceSearchQuery query, SourceRequestPurpose purpose,
        CancellationToken cancellationToken = default)
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
                var html = await PostAsync("cc100InterfaceSrch.action", SearchForm(query, page), json: false, purpose,
                    text => SnuSearchParser.Parse(text, query.Term), cancellationToken).ConfigureAwait(false);
                var parsed = html.Data;
                fetched.Add(page);
                foreach (var section in parsed.Sections)
                    sections[section.Identity] = section with { ObservedAtUtc = html.BodyReceivedAtUtc };
                if (parsed.Pages.Any(number => number < 1 || number > limits.MaxPages))
                {
                    failures.Add(new(SourceFailureKind.LimitReached, "Search exceeds the configured page bound; refine the filters."));
                    break;
                }
                foreach (var number in parsed.Pages.Where(number => !fetched.Contains(number))) pending.Add(number);
            }
            catch (SnuSourceException error) when (fetched.Count > 0)
            {
                failures.Add(new(error.Kind, error.Message, error.RetryAfter) { HttpStatusCode = error.HttpStatusCode });
                break;
            }
        }
        return new(Array.AsReadOnly(sections.Values.ToArray()), failures.Count == 0,
            Array.AsReadOnly(failures.ToArray()), fetched.Count)
        { CompletedAtUtc = timeProvider.GetUtcNow().ToUniversalTime() };
    }

    /// <summary>Returns public details only when every raw identity field agrees with the requested section.</summary>
    public Task<SourceDetails> GetDetailsAsync(SourceSectionIdentity identity, CancellationToken cancellationToken = default) =>
        GetDetailsAsync(identity, SourceRequestPurpose.Interactive, cancellationToken);

    public async Task<SourceDetails> GetDetailsAsync(SourceSectionIdentity identity, SourceRequestPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var text = await PostAsync("cc101ajax.action", new Dictionary<string, string>
        {
            ["workType"] = "S", ["openSchyy"] = identity.Year.ToString(CultureInfo.InvariantCulture),
            ["openShtmFg"] = identity.SemesterCode, ["openDetaShtmFg"] = identity.DetailSemesterCode,
            ["sbjtCd"] = identity.CourseCode, ["ltNo"] = identity.Section, ["sbjtSubhCd"] = identity.SubsubjectCode
        }, json: true, purpose, text => SnuJsonParser.Details(text, identity), cancellationToken).ConfigureAwait(false);
        return text.Data;
    }

    private static Dictionary<string, string> SearchForm(SourceSearchQuery query, int page) => new()
    {
        ["workType"] = "S", ["pageNo"] = page.ToString(CultureInfo.InvariantCulture),
        ["srchCurrPage"] = page.ToString(CultureInfo.InvariantCulture), ["srchPageSize"] = "10",
        ["srchOpenSchyy"] = query.Term.Year.ToString(CultureInfo.InvariantCulture), ["srchOpenShtm"] = query.Term.Selector,
        ["srchSbjtCd"] = query.CourseCode ?? "", ["srchSbjtNm"] = query.Name ?? "", ["srchProfNm"] = query.Instructor ?? "",
        ["srchOpenUpDeptCd"] = query.UpDepartment ?? "", ["srchOpenDeptCd"] = query.Department ?? "", ["srchOpenMjCd"] = query.Major ?? ""
    };

    private sealed record ResponsePayload<T>(T Data, DateTimeOffset BodyReceivedAtUtc);

    private async Task<ResponsePayload<T>> PostAsync<T>(string action, Dictionary<string, string> form, bool json,
        SourceRequestPurpose purpose, Func<string, T> parse, CancellationToken cancellationToken)
    {
        var kind = action switch { "cc100ajax.action" => SourceRequestKind.Catalog, "cc101ajax.action" => SourceRequestKind.Detail, _ => SourceRequestKind.SearchPage };
        try
        {
            return await requests.DispatchAsync(purpose, token => storedPolicy is null
                ? SendAsync(action, form, json, parse, new SourceTransportContext(), token)
                : storedPolicy.ExecuteAsync(kind, (facts, inner) => SendAsync(action, form, json, parse, facts, inner), token), cancellationToken).ConfigureAwait(false);
        }
        catch (SourcePolicyBlockedException blocked)
        { throw new SnuSourceException(blocked.Kind, "Saved source policy currently blocks this request.", blocked.RetryAfter); }
    }

    private async Task<ResponsePayload<T>> SendAsync<T>(string action, Dictionary<string, string> form, bool json,
        Func<string, T> parse, SourceTransportContext facts, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = new CancellationTokenSource(limits.RequestTimeout ?? TimeSpan.FromSeconds(20), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var requestToken = linked.Token;
            var uri = new Uri($"{Base}/sugang/cc/{action}");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
            request.Headers.UserAgent.ParseAdd("SNU-Watcher/0.1 (anonymous public course monitor)");
            request.Headers.Referrer = new Uri(action == "cc101ajax.action" ? $"{Base}/sugang/cc/cc100InterfaceSrch.action" : $"{Base}/sugang/co/co100.action");
            using var response = await StartTransport(request, requestToken, facts).ConfigureAwait(false);
            var headersReceivedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
            facts.Headers(headersReceivedAtUtc, (int)response.StatusCode);
            try { ValidateResponse(response, uri, json, headersReceivedAtUtc); }
            catch (SnuSourceException failure) { facts.Classified(failure); throw; }
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, requestToken).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > limits.MaxResponseBytes) throw TooLarge();
                buffer.Write(bytes, 0, count);
            }
            // Stamp inside the bounded transport operation, before decode, disposal or a later page can delay the caller.
            var receivedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
            facts.Body(receivedAtUtc);
            requestToken.ThrowIfCancellationRequested();
            T data;
            try { data = parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length))); }
            catch (SnuSourceException failure) { facts.Classified(failure); throw; }
            requestToken.ThrowIfCancellationRequested();
            // Strict endpoint parsing happens while the shared slot is held. Unknown HTML remains ParseChanged;
            // there is no verified official HTTP-200 maintenance signature to classify automatically yet.
            return new(data, receivedAtUtc);
        }
        catch (Exception error) when (error is HttpRequestException || error is IOException && error is not SourcePolicyBlockedException)
        { facts.NetworkFailure(); throw new SnuSourceException(SourceFailureKind.Unavailable, "The public SNU data source could not be reached."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { facts.Timeout(); throw new SnuSourceException(SourceFailureKind.Unavailable, "The public SNU request timed out."); }
    }

    private Task<HttpResponseMessage> StartTransport(HttpRequestMessage request, CancellationToken cancellationToken, SourceTransportContext facts)
    {
        facts.BeforeStart();
        requests.RecordTransportStart();
        facts.Started(timeProvider.GetUtcNow());
        try { return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        finally { requests.CompleteTransportInitiation(); }
    }

    private void ValidateResponse(HttpResponseMessage response, Uri expected, bool json, DateTimeOffset receivedAtUtc)
    {
        if (response.RequestMessage?.RequestUri != expected || (int)response.StatusCode is >= 300 and < 400)
            throw new SnuSourceException(SourceFailureKind.AccessRestricted, "SNU redirected this request; automatic access is paused.");
        if ((int)response.StatusCode is < 100 or > 599)
            throw new SnuSourceException(SourceFailureKind.ParseChanged, "SNU returned an unsupported HTTP status.");
        if ((int)response.StatusCode == 429)
        {
            throw new SnuSourceException(SourceFailureKind.RateLimited, "SNU is limiting requests.", RetryAfter(response, receivedAtUtc))
            { HttpStatusCode = 429 };
        }
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new SnuSourceException(SourceFailureKind.AccessRestricted, "SNU public access is restricted.")
            { HttpStatusCode = (int)response.StatusCode };
        if (!response.IsSuccessStatusCode)
            throw new SnuSourceException(SourceFailureKind.Unavailable, "SNU public search is unavailable.",
                response.StatusCode == HttpStatusCode.ServiceUnavailable ? RetryAfter(response, receivedAtUtc) : null)
            { HttpStatusCode = (int)response.StatusCode };
        if (response.Content.Headers.ContentLength > limits.MaxResponseBytes) throw TooLarge();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        // The observed details action emits JSON with text/html. Only that exact action
        // accepts this legacy media type; the strict JSON/identity parser still rejects HTML gates.
        var legacyDetailsJson = expected.AbsolutePath == "/sugang/cc/cc101ajax.action" && mediaType == "text/html";
        if (json ? mediaType is not ("application/json" or "text/json") && !legacyDetailsJson : mediaType != "text/html")
            throw new SnuSourceException(SourceFailureKind.ParseChanged, "SNU returned an unexpected content type.");
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset receivedAtUtc)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date - receivedAtUtc);
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static SnuSourceException TooLarge() => new(SourceFailureKind.ResponseTooLarge, "SNU returned a response exceeding the configured size bound.");
    private static void ValidateYear(int year) { if (year is < 2013 or > 2200) throw new ArgumentOutOfRangeException(nameof(year)); }
    private static void ValidateCode(string? value) { if (!string.IsNullOrEmpty(value) && !SourceSectionIdentity.Valid(value)) throw new ArgumentException("Invalid source filter identifier."); }
    private static void ValidateText(string? value) { if (value?.Length > 200 || value?.Any(char.IsControl) == true) throw new ArgumentException("Search text must be at most 200 printable characters."); }

    public void Dispose()
    {
        requests.EnsureCanDispose();
        storedPolicy?.StopAcceptingControls();
        try { requests.Dispose(); }
        finally
        {
            try { storedPolicy?.WaitForIdleAsync().GetAwaiter().GetResult(); }
            finally { http.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        requests.EnsureCanDispose();
        storedPolicy?.StopAcceptingControls();
        try { await requests.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { if (storedPolicy is not null) await storedPolicy.WaitForIdleAsync().ConfigureAwait(false); }
            finally { http.Dispose(); }
        }
    }

    /// <summary>Explicit acknowledgement of an access restriction. Any existing cooldown remains in force.</summary>
    public void ResumeSourceAccess()
    {
        if (storedPolicy is not null) throw new InvalidOperationException("Hosted sources require ResumeSourceAccessAsync with a saved revision.");
        requests.ResumeAccess();
    }
}
