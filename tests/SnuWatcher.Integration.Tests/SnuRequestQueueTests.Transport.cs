using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SnuWatcher.Application.Sources;
using SnuWatcher.Infrastructure.Snu;
using SnuWatcher.Infrastructure.Snu.RequestQueue;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuRequestQueueTransportTests
{
    private static readonly SourceTerm Term = new(2026, "U000200002U000300001", "2학기");
    private static readonly SourceSectionIdentity Key = new(2026, "U000200002", "U000300001", "F32.102", "012", "000");

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    public void HttpStatusMetadataRejectsValuesOutsideTheHttpRange(int status)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuSourceException(SourceFailureKind.Unavailable, "fixture") { HttpStatusCode = status });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceFailure(SourceFailureKind.Unavailable, "fixture") { HttpStatusCode = status });
    }

    [Fact]
    public async Task NonstandardHttpStatusRemainsATypedSourceFailureWithoutInvalidMetadata()
    {
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)600)))), new(TimeSpan.Zero));
        var failure = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
        Assert.Equal(SourceFailureKind.ParseChanged, failure.Kind);
        Assert.Null(failure.HttpStatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(61)]
    public void PublicSourceRejectsAProductionPacingBypass(int seconds)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuCourseSource(http, new(TimeSpan.FromSeconds(seconds))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http503PreservesDeltaOrDateRetryAfterAndStopsOtherEndpoints(bool httpDate)
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, SourceQueueOptions.ForTesting(TimeSpan.Zero));
        var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        unavailable.Headers.RetryAfter = httpDate ? new RetryConditionHeaderValue(clock.GetUtcNow().AddMinutes(90)) :
            new RetryConditionHeaderValue(TimeSpan.FromMinutes(90));
        var calls = 0;
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler((_, _) =>
            Task.FromResult(++calls == 1 ? unavailable : Json(Fixture("details.json"))))), new(TimeSpan.Zero), clock, queue);
        var failure = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
        Assert.Equal(SourceFailureKind.Unavailable, failure.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), failure.RetryAfter);
        var details = source.GetDetailsAsync(Key, SourceRequestPurpose.Watch);
        Assert.Equal(TimeSpan.FromMinutes(90), await clock.NextTimerAsync(TimeSpan.FromMinutes(90)));
        clock.AdjustUtc(TimeSpan.FromDays(-1));
        clock.Advance(TimeSpan.FromMinutes(89));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Key, (await details.WaitAsync(TimeSpan.FromSeconds(5))).Identity);
    }

    [Fact]
    public async Task CatalogDetailsAndEverySearchPageShareTheSameTwoSecondStartGap()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, new(TimeSpan.FromSeconds(2)));
        var starts = new List<long>();
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler(async (request, token) =>
        {
            starts.Add(clock.GetTimestamp());
            if (request.RequestUri!.AbsolutePath.EndsWith("cc100ajax.action", StringComparison.Ordinal)) return Json(Fixture("catalog.json"));
            if (request.RequestUri.AbsolutePath.EndsWith("cc101ajax.action", StringComparison.Ordinal)) return Json(Fixture("details.json"));
            var form = await request.Content!.ReadAsStringAsync(token);
            return Html(Fixture(form.Contains("pageNo=1", StringComparison.Ordinal) ? "search-page1.html" : "search-page2.html"));
        })), new(TimeSpan.FromSeconds(2)), clock, queue);
        await source.GetCatalogAsync(2026);
        var detail = source.GetDetailsAsync(Key);
        await clock.NextTimerAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        await detail.WaitAsync(TimeSpan.FromSeconds(5));
        var search = source.SearchAsync(new(Term, CourseCode: "F32.102"), SourceRequestPurpose.Watch);
        await clock.NextTimerAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        await clock.NextTimerAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await search.WaitAsync(TimeSpan.FromSeconds(5))).IsComplete);
        Assert.Equal(new[] { 0L, TimeSpan.FromSeconds(2).Ticks, TimeSpan.FromSeconds(4).Ticks, TimeSpan.FromSeconds(6).Ticks }, starts);
    }

    [Fact]
    public async Task SlowRequestSetupCannotCollapseTheGapBetweenActualHttpStarts()
    {
        var setupCalls = 0;
        QueueClock? clock = null;
        clock = new QueueClock(due =>
        {
            if (due == TimeSpan.FromSeconds(20) && Interlocked.Increment(ref setupCalls) == 1)
                clock!.Advance(TimeSpan.FromSeconds(5));
        });
        await using var queue = new SnuRequestDispatcher(clock, new(TimeSpan.FromSeconds(2)));
        var starts = new List<long>();
        var secondStarted = Barrier();
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler((_, _) =>
        {
            starts.Add(clock.GetTimestamp());
            if (starts.Count == 2) secondStarted.TrySetResult();
            return Task.FromResult(Json(Fixture("catalog.json")));
        })), new(TimeSpan.FromSeconds(2)), clock, queue);
        await source.GetCatalogAsync(2026);
        var next = source.GetCatalogAsync(2026);
        var spacing = clock.NextTimerAsync(TimeSpan.FromSeconds(2));
        var firstOutcome = await Task.WhenAny(spacing, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstOutcome == spacing, $"HTTP started before its pacing timer; observed start ticks: {string.Join(",", starts)}");
        await spacing;
        Assert.Single(starts);
        clock.Advance(TimeSpan.FromSeconds(2));
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { TimeSpan.FromSeconds(5).Ticks, TimeSpan.FromSeconds(7).Ticks }, starts);
        Assert.Equal(clock.GetUtcNow(), queue.GetSnapshot().LastTransportStartedAtUtc);
    }

    [Fact]
    public async Task Partial503RetainsItsStatusEvenWithoutRetryAfterAndDoesNotInventACooldown()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, SourceQueueOptions.ForTesting(TimeSpan.Zero));
        var calls = 0;
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler((_, _) => Task.FromResult(
            ++calls == 1 ? Html(Fixture("search-page1.html")) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))),
            new(TimeSpan.Zero), clock, queue);
        var partial = await source.SearchAsync(new(Term, CourseCode: "F32.102"), SourceRequestPurpose.Watch);
        var failure = Assert.Single(partial.Failures);
        Assert.Equal(SourceFailureKind.Unavailable, failure.Kind);
        Assert.Equal(503, failure.HttpStatusCode);
        Assert.Null(failure.RetryAfter);
        Assert.Equal(TimeSpan.Zero, queue.GetSnapshot().CooldownRemaining);
    }

    [Fact]
    public async Task AnOpenBodyStreamKeepsOtherHttpRequestsOutOfTheLane()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, SourceQueueOptions.ForTesting(TimeSpan.Zero));
        var entered = Barrier();
        var release = Barrier();
        var calls = 0;
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler((_, _) => Task.FromResult(
            ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new HeldContent(Fixture("catalog.json"), entered, release) } :
            Json(Fixture("details.json"))))), new(TimeSpan.Zero), clock, queue);
        var first = source.GetCatalogAsync(2026);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = source.GetDetailsAsync(Key);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(1, calls);
            Assert.False(next.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.NotEmpty((await first).Terms);
        Assert.Equal(Key, (await next.WaitAsync(TimeSpan.FromSeconds(5))).Identity);
    }

    [Fact]
    public async Task PageTwo429StopsAnAlreadyQueuedDetailRequestBeforeTheSlotIsReleased()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, SourceQueueOptions.ForTesting(TimeSpan.Zero));
        var pageTwoEntered = Barrier();
        var release = Barrier();
        var calls = 0;
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler(async (_, token) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) return Html(Fixture("search-page1.html"));
            if (call == 2)
            {
                pageTwoEntered.SetResult();
                await release.Task.WaitAsync(token);
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return limited;
            }
            return Json(Fixture("details.json"));
        })), new(TimeSpan.Zero), clock, queue);
        var search = source.SearchAsync(new(Term, CourseCode: "F32.102"), SourceRequestPurpose.Watch);
        await pageTwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var detail = source.GetDetailsAsync(Key);
        release.SetResult();
        var partial = await search;
        Assert.False(partial.IsComplete);
        Assert.Single(partial.Sections);
        Assert.Equal(SourceFailureKind.RateLimited, Assert.Single(partial.Failures).Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), await clock.NextTimerAsync(TimeSpan.FromSeconds(120)));
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(Key, (await detail.WaitAsync(TimeSpan.FromSeconds(5))).Identity);
    }

    [Fact]
    public async Task SearchPaginationRetainsWatchPurposeWhenInteractiveWorkArrives()
    {
        var clock = new QueueClock();
        var pageTwoQueued = Barrier();
        var watchPagesQueued = 0;
        await using var queue = new SnuRequestDispatcher(clock, SourceQueueOptions.ForTesting(TimeSpan.Zero), purpose =>
        {
            if (purpose == SourceRequestPurpose.Watch && Interlocked.Increment(ref watchPagesQueued) == 2) pageTwoQueued.TrySetResult();
        });
        var entered = Barrier();
        var release = Barrier();
        var firstCatalogEntered = Barrier();
        var releaseFirstCatalog = Barrier();
        var order = new List<string>();
        var catalogCount = 0;
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("cc100ajax.action", StringComparison.Ordinal))
            {
                order.Add("catalog" + ++catalogCount);
                if (catalogCount == 1) { firstCatalogEntered.SetResult(); await releaseFirstCatalog.Task.WaitAsync(token); }
                return Json(Fixture("catalog.json"));
            }
            var form = await request.Content!.ReadAsStringAsync(token);
            if (form.Contains("pageNo=1", StringComparison.Ordinal))
            {
                order.Add("search1"); entered.SetResult(); await release.Task.WaitAsync(token);
                return Html(Fixture("search-page1.html"));
            }
            order.Add("search2");
            return Html(Fixture("search-page2.html"));
        })), new(TimeSpan.Zero), clock, queue);
        var search = source.SearchAsync(new(Term, CourseCode: "F32.102"), SourceRequestPurpose.Watch);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var catalogs = new[] { source.GetCatalogAsync(2026), source.GetCatalogAsync(2026), source.GetCatalogAsync(2026) };
        release.SetResult();
        await firstCatalogEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await pageTwoQueued.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { releaseFirstCatalog.TrySetResult(); }
        await search.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(catalogs);
        Assert.Equal(new[] { "search1", "catalog1", "catalog2", "search2", "catalog3" }, order);
    }

    [Fact]
    public async Task UnknownHtmlIsParseChangedWithoutInventingAMaintenanceSignature()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, SourceQueueOptions.ForTesting(TimeSpan.Zero));
        var calls = 0;
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler((_, _) => Task.FromResult(
            ++calls == 1 ? Html("<html><title>Maintenance</title><body>Unknown template</body></html>") : Json(Fixture("catalog.json"))))),
            new(TimeSpan.Zero), clock, queue);
        var failure = await Assert.ThrowsAsync<SnuSourceException>(() => source.SearchAsync(new(Term, CourseCode: "F32.102")));
        Assert.Equal(SourceFailureKind.ParseChanged, failure.Kind);
        Assert.Equal(TimeSpan.Zero, queue.GetSnapshot().CooldownRemaining);
        Assert.NotEmpty((await source.GetCatalogAsync(2026)).Terms);
    }

    [Fact]
    public async Task BodyDeadlineCancellationRetainsTheSlotUntilTheHandlerActuallyReturns()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, SourceQueueOptions.ForTesting(TimeSpan.Zero));
        var entered = Barrier();
        var canceled = Barrier();
        var release = Barrier();
        var calls = 0;
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(new Handler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Json(Fixture("catalog.json"));
            using var registration = token.Register(() => canceled.TrySetResult());
            entered.SetResult();
            await release.Task; // Deliberately ignores cancellation until the test releases the callback.
            token.ThrowIfCancellationRequested();
            return Json(Fixture("catalog.json"));
        })), new(TimeSpan.Zero, RequestTimeout: TimeSpan.FromSeconds(10)), clock, queue);
        var first = source.GetCatalogAsync(2026);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = source.GetCatalogAsync(2026);
        // The request's real timer is supplied by TimeProvider, not a wall-clock sleep.
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(10), await clock.NextTimerAsync());
            clock.Advance(TimeSpan.FromSeconds(10));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calls);
            Assert.False(next.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(SourceFailureKind.Unavailable, (await Assert.ThrowsAsync<SnuSourceException>(() => first)).Kind);
        Assert.NotEmpty((await next.WaitAsync(TimeSpan.FromSeconds(5))).Terms);
    }

    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class HeldContent : HttpContent
    {
        private readonly Stream stream;
        public HeldContent(string text, TaskCompletionSource entered, TaskCompletionSource release)
        {
            stream = new HeldStream(Encoding.UTF8.GetBytes(text), entered, release);
            Headers.ContentType = new("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
    }

    private sealed class HeldStream(byte[] bytes, TaskCompletionSource entered, TaskCompletionSource release) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return base.Read(buffer.Span);
        }
    }
}
