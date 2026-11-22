using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Sources;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class RuntimeInteractiveQueryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task InteractiveFacadePreservesExactQueriesIdentityResultsAndOriginalSourceTimes()
    {
        await using var fixture = new InteractiveRuntimeFixture();
        var catalog = new CatalogQuery(2026, "21", "C01", "D01");
        var search = new CourseSearchQuery(2026, "21", name: "통계학", instructor: "이권상");
        var key = MonitorFixture.Key();
        var catalogResult = ScriptedRuntimeCourseSource.CatalogResult(catalog, MonitorFixture.Now.AddDays(-1));
        var searchResult = MonitorFixture.Rows(search, [key], MonitorFixture.Now.AddMinutes(-2));
        var detailsResult = ScriptedRuntimeCourseSource.Failure<CourseDetails>();
        fixture.Source.Catalog = (_, _) => Task.FromResult(catalogResult);
        fixture.Source.Search = (_, _) => Task.FromResult(searchResult);
        fixture.Source.Details = (_, _) => Task.FromResult(detailsResult);
        await fixture.Session.StartAsync();
        Assert.Same(fixture.Session, fixture.Session.Courses);
        Assert.Same(catalogResult, await fixture.Session.Courses.GetCatalogAsync(catalog));
        Assert.Same(searchResult, await fixture.Session.Courses.SearchAsync(search));
        Assert.Same(detailsResult, await fixture.Session.Courses.GetDetailsAsync(key));
        var calls = fixture.Source.Calls.ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Same(catalog, calls[0].Query);
        Assert.Same(search, calls[1].Query);
        Assert.Same(key, calls[2].Query);
        Assert.All(calls, call => Assert.Equal(SourceRequestPurpose.Interactive, call.Purpose));
        Assert.Empty(fixture.Store.Issues);
        Assert.Null(fixture.Session.Failure);
    }

    [Fact]
    public async Task TypedSourceFailureIsOrdinaryAndPreCanceledCallNeverInvokesGateway()
    {
        await using var fixture = new InteractiveRuntimeFixture();
        fixture.Source.Search = (_, _) => Task.FromResult(ScriptedRuntimeCourseSource.Failure<CourseSearchData>());
        await fixture.Session.StartAsync();
        var query = new CourseSearchQuery(2026, "21", courseCode: "F32.102");
        Assert.Equal(SourceReadStatus.Failed, (await fixture.Session.Courses.SearchAsync(query)).Status);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Session.Courses.SearchAsync(query, canceled.Token));
        Assert.Single(fixture.Source.Calls);
        Assert.Equal(RuntimeSessionState.Running, fixture.Session.State);
        Assert.Null(fixture.Session.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedQueryExceptionsAreSanitizedActorFaults(bool invalidOperation)
    {
        await using var fixture = new InteractiveRuntimeFixture();
        fixture.Source.Catalog = (_, _) =>
        {
            if (invalidOperation) throw new InvalidOperationException("private source credentials");
            throw new IOException("private source path");
        };
        await fixture.Session.StartAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Courses.GetCatalogAsync(new(2026)));
        Assert.DoesNotContain("private", error.Message);
        Assert.Null(error.InnerException);
        var failure = await fixture.Session.FailureTask.WaitAsync(Timeout);
        Assert.Equal(RuntimeFailureKind.Actor, failure.Kind);
        Assert.Equal("runtime_query_failed", failure.Code);
        Assert.True((await fixture.Session.StopAsync().WaitAsync(Timeout)).IsFinalized);
    }

    [Fact]
    public async Task CanceledAttachedUiWaiterSignalsOnlyItsSourceTokenWhileSessionRetainsActualLogicalWork()
    {
        await using var fixture = new InteractiveRuntimeFixture(new(maxAdmittedCommands: 1));
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SourceRead<CourseSearchData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.Search = async (_, token) =>
        {
            using var registration = token.Register(() => tokenCanceled.TrySetResult());
            entered.TrySetResult(token);
            return await release.Task;
        };
        await fixture.Session.StartAsync();
        var query = new CourseSearchQuery(2026, "21", courseCode: "F32.102");
        var actualResult = MonitorFixture.Rows(query, [MonitorFixture.Key()], MonitorFixture.Now);
        using var caller = new CancellationTokenSource();
        var read = fixture.Session.Courses.SearchAsync(query, caller.Token);
        Task<RuntimeSessionStopResult>? stopping = null;
        try
        {
            var actualToken = await entered.Task.WaitAsync(Timeout);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            await tokenCanceled.Task.WaitAsync(Timeout);
            Assert.True(actualToken.IsCancellationRequested);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Courses.GetCatalogAsync(new(2026)));
            Assert.Null(fixture.Session.Failure);
            stopping = fixture.Session.StopAsync();
            Assert.False(stopping.IsCompleted);
        }
        finally { release.TrySetResult(actualResult); }
        Assert.True((await stopping!.WaitAsync(Timeout)).IsFinalized);
        Assert.Single(fixture.Source.Calls);
    }

    [Fact]
    public async Task CancelingOneQueryDoesNotCancelAnotherAdmittedQueryOrTheRuntimeLifetime()
    {
        await using var fixture = new InteractiveRuntimeFixture();
        var firstEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource<SourceRead<CourseSearchData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource<SourceRead<CourseSearchData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstQuery = new CourseSearchQuery(2026, "21", courseCode: "F32.102");
        var secondQuery = new CourseSearchQuery(2026, "21", courseCode: "F35.105");
        var firstResult = MonitorFixture.Rows(firstQuery, [MonitorFixture.Key()], MonitorFixture.Now);
        var secondResult = MonitorFixture.Rows(secondQuery, [MonitorFixture.Key()], MonitorFixture.Now);
        fixture.Source.Search = (query, token) =>
        {
            if (query == firstQuery) { firstEntered.TrySetResult(token); return firstRelease.Task; }
            secondEntered.TrySetResult(token);
            return secondRelease.Task;
        };
        await fixture.Session.StartAsync();
        using var caller = new CancellationTokenSource();
        var first = fixture.Session.Courses.SearchAsync(firstQuery, caller.Token);
        var second = fixture.Session.Courses.SearchAsync(secondQuery);
        try
        {
            var firstToken = await firstEntered.Task.WaitAsync(Timeout);
            var secondToken = await secondEntered.Task.WaitAsync(Timeout);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.True(firstToken.IsCancellationRequested);
            Assert.False(secondToken.IsCancellationRequested);
            secondRelease.TrySetResult(secondResult);
            Assert.Same(secondResult, await second.WaitAsync(Timeout));
            Assert.Equal(RuntimeSessionState.Running, fixture.Session.State);
            Assert.Null(fixture.Session.Failure);
        }
        finally { firstRelease.TrySetResult(firstResult); secondRelease.TrySetResult(secondResult); }
    }

    [Fact]
    public async Task StopCancelsOwnedQueryClosesAdmissionAndAwaitsIgnoringSourceBeforeFinalizing()
    {
        await using var fixture = new InteractiveRuntimeFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SourceRead<CourseCatalogData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.Catalog = async (_, token) =>
        {
            using var registration = token.Register(() => tokenCanceled.TrySetResult());
            entered.TrySetResult();
            return await release.Task;
        };
        await fixture.Session.StartAsync();
        var query = new CatalogQuery(2026);
        var result = ScriptedRuntimeCourseSource.CatalogResult(query);
        var read = fixture.Session.Courses.GetCatalogAsync(query);
        Task<RuntimeSessionStopResult>? stopping = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            stopping = fixture.Session.StopAsync();
            await tokenCanceled.Task.WaitAsync(Timeout);
            Assert.False(stopping.IsCompleted);
            Assert.False(read.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Courses.GetDetailsAsync(MonitorFixture.Key()));
        }
        finally { release.TrySetResult(result); }
        Assert.Same(result, await read.WaitAsync(Timeout));
        Assert.True((await stopping!.WaitAsync(Timeout)).IsFinalized);
        Assert.Single(fixture.Source.Calls);
    }

    [Fact]
    public async Task TypedFailureStillSurfacesStickyLocalPolicyStorageFaultWhenNoWatchesExist()
    {
        string? diagnostic = null;
        await using var fixture = new InteractiveRuntimeFixture(diagnostic: () => Volatile.Read(ref diagnostic));
        fixture.Source.Search = (_, _) =>
        {
            Volatile.Write(ref diagnostic, "source_policy_storage_failed");
            return Task.FromResult(ScriptedRuntimeCourseSource.Failure<CourseSearchData>());
        };
        await fixture.Session.StartAsync();
        var read = await fixture.Session.Courses.SearchAsync(new(2026, "21", courseCode: "F32.102"));
        Assert.Equal(SourceReadStatus.Failed, read.Status);
        var failure = await fixture.Session.FailureTask.WaitAsync(Timeout);
        Assert.Equal(RuntimeFailureKind.SavedState, failure.Kind);
        Assert.Equal("source_policy_storage_failed", failure.Code);
        Assert.True((await fixture.Session.StopAsync().WaitAsync(Timeout)).IsFinalized);
    }
}
