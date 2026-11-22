using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SnuWatcher.Infrastructure.Snu;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuTransportTimestampTests
{
    private static readonly SourceTerm Term = new(2026, "U000200002U000300001", "2학기");
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EachPageKeepsItsBodyReceiptTimeBeforeStreamDisposalAndFinalCompletion()
    {
        var clock = new ReceiptClock(Start);
        using var source = Source(clock, Page(clock, Fixture("search-page1.html"), 1),
            Page(clock, Fixture("search-page2.html"), 5));

        var result = await source.SearchAsync(new(Term, CourseCode: "F32.102"));

        Assert.Equal(Start.AddSeconds(1), result.Sections.Single(s => s.Identity.Section == "001").ObservedAtUtc);
        Assert.Equal(Start.AddSeconds(16), result.Sections.Single(s => s.Identity.Section == "012").ObservedAtUtc);
        Assert.Equal(Start.AddSeconds(26), result.CompletedAtUtc);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.PagesFetched);
    }

    [Fact]
    public async Task AFailedLaterPageDoesNotMakeEarlierCountsLookFreshAgain()
    {
        var clock = new ReceiptClock(Start);
        var failure = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var handler = new Responses((_, number) =>
        {
            if (number == 1) return Page(clock, Fixture("search-page1.html"), 1);
            clock.Set(Start.AddMinutes(3));
            return failure;
        });
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(handler), new(TimeSpan.Zero), clock);

        var result = await source.SearchAsync(new(Term, CourseCode: "F32.102"));

        Assert.False(result.IsComplete);
        Assert.Equal(Start.AddSeconds(1), Assert.Single(result.Sections).ObservedAtUtc);
        Assert.Equal(Start.AddMinutes(3), result.CompletedAtUtc);
        Assert.Equal(SourceFailureKind.Unavailable, Assert.Single(result.Failures).Kind);
    }

    [Fact]
    public async Task ReplacingARepeatedIdentityReplacesItsCountsAndReceiptTogether()
    {
        var clock = new ReceiptClock(Start);
        var repeated = Fixture("search-page2.html").Replace("012", "001", StringComparison.Ordinal);
        using var source = Source(clock, Page(clock, Fixture("search-page1.html"), 1), Page(clock, repeated, 5));

        var result = await source.SearchAsync(new(Term, CourseCode: "F32.102"));

        var retained = Assert.Single(result.Sections);
        Assert.Equal(39, retained.Registered);
        Assert.Equal(Start.AddSeconds(16), retained.ObservedAtUtc);
        Assert.Equal(Start.AddSeconds(26), result.CompletedAtUtc);
    }

    [Fact]
    public void PureParserAndManuallyConstructedResultsRemainExplicitlyUnstamped()
    {
        var page = SnuSearchParser.Parse(Fixture("search-page1.html"), Term);
        Assert.All(page.Sections, row => Assert.Null(row.ObservedAtUtc));
        var result = new SourceSearchResult(page.Sections, true, [], 1);
        Assert.Null(result.CompletedAtUtc);
    }

    [Fact]
    public async Task CompletedEmptySearchStillHasAnOutcomeTime()
    {
        var clock = new ReceiptClock(Start);
        using var source = Source(clock, Page(clock, "<html><head><title>Search Results</title></head><body>No courses found</body></html>", 2));
        var result = await source.SearchAsync(new(Term, Name: "No such class"));
        Assert.Empty(result.Sections);
        Assert.True(result.IsComplete);
        Assert.Equal(Start.AddSeconds(12), result.CompletedAtUtc);
    }

    [Fact]
    public async Task ReceiptAndCompletionAreNormalizedToUtc()
    {
        var offsetNow = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(9));
        var clock = new ReceiptClock(offsetNow);
        using var source = Source(clock, Page(clock, Fixture("search-page2.html"), 2));
        var result = await source.SearchAsync(new(Term, CourseCode: "F32.102"));
        Assert.Equal(Start.AddSeconds(2), Assert.Single(result.Sections).ObservedAtUtc);
        Assert.Equal(TimeSpan.Zero, result.Sections[0].ObservedAtUtc!.Value.Offset);
        Assert.Equal(Start.AddSeconds(12), result.CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, result.CompletedAtUtc!.Value.Offset);
    }

    [Fact]
    public async Task RetryAfterHttpDateUsesTheInjectedUtcClock()
    {
        var clock = new ReceiptClock(Start);
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Start.AddMinutes(2));
        using var source = Source(clock, response);
        var failure = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
        Assert.Equal(TimeSpan.FromMinutes(2), failure.RetryAfter);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static HttpResponseMessage Page(ReceiptClock clock, string text, int bodySeconds) => new(HttpStatusCode.OK)
    {
        Content = new TrackedContent(text, () => clock.Advance(bodySeconds), () => clock.Advance(10)),
    };

    private static SnuCourseSource Source(ReceiptClock clock, params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        return SnuCourseSource.CreateForTesting(new HttpClient(new Responses((_, _) => queue.Dequeue())), new(TimeSpan.Zero), clock);
    }

    private sealed class ReceiptClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
        public void Set(DateTimeOffset value) => _now = value;
    }

    private sealed class Responses(Func<HttpRequestMessage, int, HttpResponseMessage> next) : HttpMessageHandler
    {
        private int _number;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = next(request, ++_number);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class TrackedContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly Action _bodyReceived;
        private readonly Action _disposed;

        public TrackedContent(string text, Action bodyReceived, Action disposed)
        {
            _bytes = Encoding.UTF8.GetBytes(text);
            _bodyReceived = bodyReceived;
            _disposed = disposed;
            Headers.ContentType = new("text/html");
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new TrackedStream(_bytes, _bodyReceived, _disposed));
    }

    private sealed class TrackedStream(byte[] bytes, Action bodyReceived, Action disposed) : MemoryStream(bytes)
    {
        private bool _received;
        private bool _disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = base.Read(buffer.Span);
            if (count == 0 && !_received) { _received = true; bodyReceived(); }
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _disposed = true; disposed(); }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
