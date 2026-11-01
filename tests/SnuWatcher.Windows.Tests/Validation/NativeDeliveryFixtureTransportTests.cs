using SnuWatcher.Windows.Validation;
using Xunit;

namespace SnuWatcher.Windows.Tests.Validation;

public sealed class NativeDeliveryFixtureTransportTests
{
    [Fact]
    public async Task FixedRequestsReturnOnlyTheFullThenOpenFixtureAndCannotReturnToFull()
    {
        using var transport = new NativeDeliveryFixtureTransport();
        using var client = new HttpClient(transport);
        var catalog = await client.PostAsync(NativeDeliveryFixtureTransport.CatalogUri, Form(false));
        Assert.Contains("U000200002U000300002", await catalog.Content.ReadAsStringAsync());
        var full = await client.PostAsync(NativeDeliveryFixtureTransport.SearchUri, Form(true));
        Assert.Contains("40/40 (40)", await full.Content.ReadAsStringAsync());
        transport.OpenOneSeat();
        transport.OpenOneSeat();
        var open = await client.PostAsync(NativeDeliveryFixtureTransport.SearchUri, Form(true));
        Assert.Contains("39/40 (40)", await open.Content.ReadAsStringAsync());
        Assert.Equal(1, transport.FullSearches);
        Assert.Equal(1, transport.OpenSearches);
        Assert.Equal(3, transport.Requests);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("get")]
    [InlineData("detail")]
    [InlineData("section_alias")]
    [InlineData("duplicate")]
    [InlineData("blank_term")]
    public async Task UnexpectedRequestsFailLocallyWithNoFallback(string mode)
    {
        using var transport = new NativeDeliveryFixtureTransport();
        using var client = new HttpClient(transport);
        var uri = mode == "blank_term" ? NativeDeliveryFixtureTransport.CatalogUri : mode == "host" ? "https://example.com/" : mode == "detail"
            ? "https://sugang.snu.ac.kr/sugang/cc/cc101ajax.action" : NativeDeliveryFixtureTransport.SearchUri;
        using var request = new HttpRequestMessage(mode == "get" ? HttpMethod.Get : HttpMethod.Post, uri) { Content = Form(mode != "blank_term", mode) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request));
        Assert.Equal(1, transport.RejectedRequests);
        Assert.Equal(0, transport.FullSearches);
        Assert.Equal(0, transport.OpenSearches);
    }

    private static FormUrlEncodedContent Form(bool search, string? mode = null)
    {
        var values = search ? new Dictionary<string, string>
        {
            ["workType"] = "S", ["pageNo"] = "1", ["srchCurrPage"] = "1", ["srchPageSize"] = "10",
            ["srchOpenSchyy"] = "2026", ["srchOpenShtm"] = "U000200002U000300002", ["srchSbjtCd"] = "F32.102",
            ["srchSbjtNm"] = "", ["srchProfNm"] = "", ["srchOpenUpDeptCd"] = "", ["srchOpenDeptCd"] = "", ["srchOpenMjCd"] = "",
        } : new Dictionary<string, string> { ["openUpDeptCd"] = "", ["openDeptCd"] = "" };
        if (mode == "blank_term") { values["srchOpenSchyy"] = "2026"; values["srchOpenShtm"] = ""; }
        if (mode == "section_alias") values["srchSbjtCd"] = "OTHER";
        return new(mode == "duplicate" ? values.Concat([new KeyValuePair<string, string>("pageNo", "1")]) : values);
    }
}
