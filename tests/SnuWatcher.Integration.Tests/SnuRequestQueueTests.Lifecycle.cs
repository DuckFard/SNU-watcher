using System.Net;
using System.Text;
using SnuWatcher.Application.Sources;
using SnuWatcher.Infrastructure.Snu;
using SnuWatcher.Infrastructure.Snu.RequestQueue;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuRequestQueueLifecycleTests
{
    [Fact]
    public async Task SourceAsyncShutdownRetainsOwnedClientUntilItsActualHandlerStops()
    {
        var entered = Barrier();
        var release = Barrier();
        var handler = new OwnedHandler(async (_, token) =>
        {
            entered.SetResult();
            await release.Task; // This fixture deliberately delays after cancellation.
            token.ThrowIfCancellationRequested();
            return Json();
        });
        var source = SnuCourseSource.CreateForTesting(new HttpClient(handler), new(TimeSpan.Zero));
        var read = source.GetCatalogAsync(2026);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = source.DisposeAsync().AsTask();
        try
        {
            Assert.False(shutdown.IsCompleted);
            Assert.False(handler.IsDisposed);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handler.IsDisposed);
        await source.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitSourceResumeAllowsAccessAgainWithoutBypassingExistingCooldown()
    {
        var clock = new QueueClock();
        await using var queue = new SnuRequestDispatcher(clock, SourceQueueOptions.ForTesting(TimeSpan.Zero));
        var calls = 0;
        await using var source = SnuCourseSource.CreateForTesting(new HttpClient(new OwnedHandler((_, _) =>
            Task.FromResult(++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json()))), new(TimeSpan.Zero), clock, queue);
        Assert.Equal(SourceFailureKind.AccessRestricted, (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026))).Kind);
        Assert.Equal(SourceFailureKind.AccessRestricted, (await Assert.ThrowsAsync<SnuSourceException>(() =>
            source.GetCatalogAsync(2026, SourceRequestPurpose.Watch))).Kind);
        Assert.Equal(1, calls);
        queue.DeclareMaintenance(TimeSpan.FromSeconds(60));
        source.ResumeSourceAccess();
        var resumed = source.GetCatalogAsync(2026, SourceRequestPurpose.Watch);
        await clock.NextTimerAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.NotEmpty((await resumed.WaitAsync(TimeSpan.FromSeconds(5))).Terms);
    }

    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog.json")), Encoding.UTF8, "application/json")
    };

    private sealed class OwnedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) IsDisposed = true; base.Dispose(disposing); }
    }
}
