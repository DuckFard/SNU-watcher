using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Snu;
using SnuWatcher.Infrastructure.Snu.Gateway;

namespace SnuWatcher.Integration.Tests;

public sealed partial class SnuGatewayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly SectionKey Key = new(2026, "U000200002", "U000300001", "F32.102", "012", "000");
    private static readonly CourseSearchQuery Query = new(2026, "U000200002U000300001", "F32.102");

    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static HttpResponseMessage Json(string text) => Response(text, "application/json");
    private static HttpResponseMessage Html(string text) => Response(text, "text/html");
    private static HttpResponseMessage Response(string text, string type) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type) };

    private sealed class Harness : IAsyncDisposable
    {
        public GatewayClock Clock { get; } = new();
        public GatewayHandler Handler { get; }
        public SnuCourseSource Source { get; }
        public SnuCourseGateway Gateway { get; }

        public Harness(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>>? respond = null,
            SnuGatewayOptions? options = null)
        {
            Handler = new(respond ?? DefaultResponse);
            Source = SnuCourseSource.CreateForTesting(new HttpClient(Handler), new(TimeSpan.Zero), Clock);
            Gateway = new(Source, Clock, options);
        }

        public async ValueTask DisposeAsync()
        {
            await Gateway.DisposeAsync();
            await Source.DisposeAsync();
        }
    }

    private static async Task<HttpResponseMessage> DefaultResponse(HttpRequestMessage request, int number, CancellationToken token)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("cc100ajax.action", StringComparison.Ordinal)) return Json(Fixture("catalog.json"));
        if (request.RequestUri.AbsolutePath.EndsWith("cc101ajax.action", StringComparison.Ordinal))
        {
            var fields = (await request.Content!.ReadAsStringAsync(token)).Split('&').Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]), StringComparer.Ordinal);
            return Json(Fixture("details.json").Replace("\"012\"", $"\"{fields["ltNo"]}\"", StringComparison.Ordinal)
                .Replace("\"000\"", $"\"{fields["sbjtSubhCd"]}\"", StringComparison.Ordinal));
        }
        return Html(Fixture("search-page2.html"));
    }

    private sealed class GatewayHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        public int Catalogs => Count("cc100ajax.action");
        public int Searches => Count("cc100InterfaceSrch.action");
        public int Details => Count("cc101ajax.action");
        private int Count(string action) => _counts.GetValueOrDefault(action);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = request.RequestUri!.Segments[^1];
            var number = _counts.AddOrUpdate(action, 1, (_, value) => value + 1);
            var response = await respond(request, number, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class GatewayClock : TimeProvider
    {
        private readonly FakeTimeProvider _monotonic = new(Start);
        private DateTimeOffset _utc = Start;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _monotonic.GetTimestamp();
        public override long TimestampFrequency => _monotonic.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _monotonic.CreateTimer(callback, state, dueTime, period);
        public void Advance(TimeSpan elapsed) { _utc += elapsed; _monotonic.Advance(elapsed); }
        public void AdjustUtc(TimeSpan delta) => _utc += delta;
    }
}
