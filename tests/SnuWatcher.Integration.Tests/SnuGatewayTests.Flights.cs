using System.Net;
using SnuWatcher.Application.Sources;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed partial class SnuGatewayTests
{
    [Fact]
    public async Task CatalogCoalescesAcrossPurposeAndCancelingOneCallerDoesNotCancelAnother()
    {
        var started = Barrier(); var release = Barrier(); CancellationToken underlying = default;
        await using var fixture = new Harness(async (request, number, token) =>
        { underlying = token; started.SetResult(); await release.Task; return await DefaultResponse(request, number, token); });
        using var canceled = new CancellationTokenSource();
        var first = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, canceled.Token);
        await started.Task.WaitAsync(Timeout);
        var second = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Watch, default);
        canceled.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(underlying.IsCancellationRequested);
            Assert.Equal(1, fixture.Handler.Catalogs);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(SourceReadStatus.Complete, (await second.WaitAsync(Timeout)).Status);
        Assert.Equal(1, fixture.Handler.Catalogs);
    }

    [Fact]
    public async Task InteractiveQueriesJoinAnActiveWatchButCountsAreNeverCached()
    {
        var started = Barrier(); var release = Barrier();
        await using var fixture = new Harness(async (request, number, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("cc100InterfaceSrch.action", StringComparison.Ordinal) && number == 1)
            { started.SetResult(); await release.Task; }
            return await DefaultResponse(request, number, token);
        });
        var watch = fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Watch, default);
        await started.Task.WaitAsync(Timeout);
        var interactive = fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Interactive, default);
        release.TrySetResult();
        var first = await watch.WaitAsync(Timeout);
        Assert.Same(first, await interactive.WaitAsync(Timeout));
        Assert.Equal(1, fixture.Handler.Searches);
        await fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Interactive, default);
        Assert.Equal(2, fixture.Handler.Searches);
    }

    [Fact]
    public async Task WatchAfterActiveInteractiveSearchAlwaysGetsAFreshHttpOperation()
    {
        var started = Barrier(); var release = Barrier();
        await using var fixture = new Harness(async (request, number, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("cc100InterfaceSrch.action", StringComparison.Ordinal))
                return await DefaultResponse(request, number, token);
            if (number == 1) { started.SetResult(); await release.Task; }
            return Html(Fixture("search-page2.html").Replace("39/40", number == 1 ? "39/40" : "38/40", StringComparison.Ordinal));
        });
        var ui = fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Interactive, default);
        await started.Task.WaitAsync(Timeout);
        var watch = fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Watch, default);
        release.TrySetResult();
        Assert.Equal(39, Assert.Single((await ui.WaitAsync(Timeout)).Data!.Rows).Registered);
        Assert.Equal(38, Assert.Single((await watch.WaitAsync(Timeout)).Data!.Rows).Registered);
        Assert.Equal(2, fixture.Handler.Searches);
    }

    [Fact]
    public async Task EachWatchCallIsNewAndDistinctInteractiveFiltersDoNotCoalesce()
    {
        await using var fixture = new Harness();
        await fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Watch, default);
        await fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Watch, default);
        await fixture.Gateway.SearchAsync(new(Query.Year, Query.TermSelector, Query.CourseCode, name: "통계"), SourceRequestPurpose.Interactive, default);
        Assert.Equal(3, fixture.Handler.Searches);
        Assert.Equal(1, fixture.Handler.Catalogs);
    }

    [Fact]
    public async Task ALastCanceledWaiterDoesNotFreeAnActuallyRunningFlightOrAllowNewJoins()
    {
        var started = Barrier(); var release = Barrier();
        await using var fixture = new Harness(async (request, number, token) =>
        {
            if (number == 1) { started.SetResult(); await release.Task; }
            return await DefaultResponse(request, number, token);
        }, new(maxLiveFlights: 1));
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, cancellation.Token);
        await started.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            var busy = await fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
            Assert.Equal(SourceProblemKind.LimitReached, Assert.Single(busy.Problems).Kind);
            Assert.Equal("gateway_busy", busy.Problems[0].DiagnosticCode);
            Assert.Equal(1, fixture.Handler.Catalogs);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task GlobalCallerBoundAppliesToCoalescedWaitersAndCancellationReleasesOnlyThatCaller()
    {
        var started = Barrier(); var release = Barrier();
        await using var fixture = new Harness(async (request, number, token) =>
        { started.SetResult(); await release.Task; return await DefaultResponse(request, number, token); }, new(maxCallers: 2));
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, cancellation.Token);
        await started.Task.WaitAsync(Timeout);
        var second = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
        try
        {
            var busy = await fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
            Assert.Equal("gateway_busy", Assert.Single(busy.Problems).DiagnosticCode);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            var replacement = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
            release.TrySetResult();
            Assert.Same(await second.WaitAsync(Timeout), await replacement.WaitAsync(Timeout));
            Assert.Equal(1, fixture.Handler.Catalogs);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task OneFlightAndOneCallerBudgetCanResolveColdCatalogThenRunAWatchSearch()
    {
        await using var fixture = new Harness(options: new(maxLiveFlights: 1, maxCallers: 1));
        var read = await fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Watch, default);
        Assert.Equal(SourceReadStatus.Complete, read.Status);
        Assert.Equal(1, fixture.Handler.Catalogs);
        Assert.Equal(1, fixture.Handler.Searches);
    }

    [Fact]
    public async Task AFailedLaterPageReturnsPartialWithEarlierReceiptTimeAndSanitizedRateLimit()
    {
        await using var fixture = new Harness(async (request, number, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("cc100InterfaceSrch.action", StringComparison.Ordinal))
                return await DefaultResponse(request, number, token);
            if (number == 1) return Html(Fixture("search-page1.html"));
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new(TimeSpan.FromMinutes(3));
            return limited;
        });
        var read = await fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Watch, default);
        Assert.Equal(SourceReadStatus.Partial, read.Status);
        Assert.Equal(Start, Assert.Single(read.Data!.Rows).ObservedAtUtc);
        Assert.Equal("001", read.Data.Rows[0].Key.Section);
        Assert.Equal(SourceProblemKind.RateLimited, Assert.Single(read.Problems).Kind);
        Assert.Equal(TimeSpan.FromMinutes(3), read.Problems[0].RetryAfter);
    }

    [Fact]
    public async Task ANewCallerIgnoresCanceledStillRunningFlightAndGetsItsOwnRequest()
    {
        var started = Barrier(); var release = Barrier();
        await using var fixture = new Harness(async (request, number, token) =>
        {
            if (number == 1) { started.SetResult(); await release.Task; }
            return await DefaultResponse(request, number, token);
        });
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, cancellation.Token);
        await started.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        var fresh = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Watch, default);
        release.TrySetResult();
        Assert.Equal(SourceReadStatus.Complete, (await fresh.WaitAsync(Timeout)).Status);
        Assert.Equal(2, fixture.Handler.Catalogs);
    }

    [Fact]
    public async Task IdenticalDetailFlightsCoalesceAcrossPurposeWithoutAliasingMeetings()
    {
        var started = Barrier(); var release = Barrier();
        await using var fixture = new Harness(async (request, number, token) =>
        { started.SetResult(); await release.Task; return await DefaultResponse(request, number, token); });
        var first = fixture.Gateway.GetDetailsAsync(Key, SourceRequestPurpose.Interactive, default);
        await started.Task.WaitAsync(Timeout);
        var second = fixture.Gateway.GetDetailsAsync(Key, SourceRequestPurpose.Watch, default);
        release.TrySetResult();
        Assert.Same(await first.WaitAsync(Timeout), await second.WaitAsync(Timeout));
        Assert.Equal(1, fixture.Handler.Details);
    }

    [Fact]
    public async Task FirstPageSourceFailureKeepsTypedOutcomeAndDoesNotExposeResponseBody()
    {
        await using var fixture = new Harness((request, number, token) =>
            request.RequestUri!.AbsolutePath.EndsWith("cc100InterfaceSrch.action", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private cookies and server body") })
                : DefaultResponse(request, number, token));
        var read = await fixture.Gateway.SearchAsync(Query, SourceRequestPurpose.Watch, default);
        Assert.Equal(SourceReadStatus.Failed, read.Status);
        Assert.Null(read.Data);
        Assert.Equal(SourceProblemKind.AccessRestricted, Assert.Single(read.Problems).Kind);
        Assert.Equal("source_access_restricted", read.Problems[0].DiagnosticCode);
        Assert.DoesNotContain("private", read.ToString(), StringComparison.Ordinal);
    }
}
