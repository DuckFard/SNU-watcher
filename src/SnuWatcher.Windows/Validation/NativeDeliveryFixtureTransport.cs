using System.Net;
using System.Text;
using SnuWatcher.Core;

namespace SnuWatcher.Windows.Validation;

/// <summary>Sealed, socket-free qualification transport. Only two exact anonymous POST shapes have responses.</summary>
public sealed class NativeDeliveryFixtureTransport : HttpMessageHandler
{
    /// <summary>Only accepted catalog destination; this handler never connects to it.</summary>
    public const string CatalogUri = "https://sugang.snu.ac.kr/sugang/cc/cc100ajax.action";
    /// <summary>Only accepted course search destination; this handler never connects to it.</summary>
    public const string SearchUri = "https://sugang.snu.ac.kr/sugang/cc/cc100InterfaceSrch.action";
    /// <summary>Fixed exact fixture identity, preserving section/subsubject leading zeros.</summary>
    public static SectionKey Key { get; } = new(2026, "U000200002", "U000300002", "F32.102", "012", "000");
    private int _open, _requests, _rejected, _fullSearches, _openSearches, _disposed;
    /// <summary>Total intercepted fixture requests, including rejected shapes.</summary>
    public int Requests => Volatile.Read(ref _requests);
    /// <summary>Rejected requests; any nonzero value makes qualification fail.</summary>
    public int RejectedRequests => Volatile.Read(ref _rejected);
    /// <summary>Search responses that actually contained the initial full fixture.</summary>
    public int FullSearches => Volatile.Read(ref _fullSearches);
    /// <summary>Search responses that actually contained the one-seat opening fixture.</summary>
    public int OpenSearches => Volatile.Read(ref _openSearches);
    /// <summary>Actual physical transport disposal evidence.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    /// <summary>One-way fixture transition. Counts are synthetic and must be labelled by the native payload decorator.</summary>
    public void OpenOneSeat() => Interlocked.Exchange(ref _open, 1);

    /// <summary>Validates the entire closed request, then returns fixed bytes; there is no delegated handler or socket fallback.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var number = Interlocked.Increment(ref _requests);
        try
        {
            if (IsDisposed || number > 64 || request.Method != HttpMethod.Post || request.RequestUri is null
                || request.Content?.Headers.ContentType?.MediaType != "application/x-www-form-urlencoded") throw Invalid();
            var url = request.RequestUri.AbsoluteUri;
            if (url is not (CatalogUri or SearchUri)) throw Invalid();
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 2048) throw Invalid();
            var pairs = body.Split('&').Select(pair => pair.Split('=', 2)).ToArray();
            var expected = url == CatalogUri ? CatalogForm(pairs.Length == 4) : SearchForm();
            if (pairs.Length != expected.Count || pairs.Any(pair => pair.Length != 2)) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in pairs)
            {
                var name = Decode(pair[0]); var value = Decode(pair[1]);
                if (!seen.Add(name) || !expected.TryGetValue(name, out var actual) || actual != value) throw Invalid();
            }
            var search = url == SearchUri; var open = Volatile.Read(ref _open) != 0;
            if (search) { if (open) Interlocked.Increment(ref _openSearches); else Interlocked.Increment(ref _fullSearches); }
            return new(HttpStatusCode.OK) { Content = new StringContent(search ? SearchHtml(open) : CatalogJson, Encoding.UTF8,
                search ? "text/html" : "application/json"), RequestMessage = request };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { Interlocked.Increment(ref _rejected); throw Invalid(); }
    }
    /// <summary>Records actual transport closure; no background task survives it.</summary>
    protected override void Dispose(bool disposing) { if (disposing) Interlocked.Exchange(ref _disposed, 1); base.Dispose(disposing); }
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    private static InvalidOperationException Invalid() => new("native_fixture_request_rejected");
    private static Dictionary<string, string> CatalogForm(bool selected)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["openUpDeptCd"] = "", ["openDeptCd"] = "" };
        if (selected) { fields["srchOpenSchyy"] = "2026"; fields["srchOpenShtm"] = "U000200002U000300002"; }
        return fields;
    }
    private static Dictionary<string, string> SearchForm() => new(StringComparer.Ordinal)
    {
        ["workType"] = "S", ["pageNo"] = "1", ["srchCurrPage"] = "1", ["srchPageSize"] = "10",
        ["srchOpenSchyy"] = "2026", ["srchOpenShtm"] = "U000200002U000300002", ["srchSbjtCd"] = "F32.102",
        ["srchSbjtNm"] = "", ["srchProfNm"] = "", ["srchOpenUpDeptCd"] = "", ["srchOpenDeptCd"] = "", ["srchOpenMjCd"] = "",
    };
    private const string CatalogJson = """
        {"openSchyy":"2026","currSchyy":"2026","currShtmFg":"U000200002","currDetaShtmFg":"U000300002",
         "SHTM":[{"cmmnCd":"U000200002U000300002","korNm":"테스트 표본 학기","engNm":"TEST fixture term"}],"UP_DEPT":[],"DEPT":[]}
        """;
    private static string SearchHtml(bool open) => """
        <!doctype html><html lang="ko"><head><title>검색결과 | 강좌검색 | 서울대학교 수강신청 시스템</title></head><body>
        <div class="course-info-item">
        <input name="openSchyy" value="2026"><input name="openShtmFg" value="U000200002"><input name="openDetaShtmFg" value="U000300002">
        <input name="sbjtCd" value="F32.102"><input name="ltNo" value="012"><input name="sbjtSubhCd" value="000"><input name="sbjtNm" value="TEST fixture statistics">
        <div class="course-name"><strong>TEST fixture statistics</strong></div>
        <ul class="course-info"><li class="txt"><span>TEST fixture instructor</span><span>TEST fixture department</span><span>F32.102(012)</span></li>
        <li class="txt"><span>수강신청인원/정원(재학생) <em>
        """ + (open ? "39" : "40") + """
        /40 (40)</em></span><span>총수강인원 <em>45</em></span><span>학점 <em>3</em></span><span>월(09:30~10:45)</span></li></ul>
        </div></body></html>
        """;
}
