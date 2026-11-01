using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Navigation;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Tests.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Search;

/// <summary>Deterministic UI publication and actual-operation ownership races.</summary>
public sealed class SearchSelectionRaceTests
{
    /// <summary>A failed selection dispatcher is reported safely instead of escaping an async WPF event.</summary>
    [Fact]
    public async Task FailedRowAdmissionReportsFixedFailureWithoutStartingDetails()
    {
        await using var inner = new DedicatedUiDispatcher();
        var dispatcher = new FailingDispatcher(inner);
        var queries = new SearchQueries();
        var errors = new List<string>();
        var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock(), errors.Add);
        try
        {
            await CourseSearchViewModelTests.Prepare(view, inner);
            await view.SearchCommand.ExecuteAsync();
            var row = Assert.Single(view.Model.State.Rows).Row;
            dispatcher.Fail = true;
            Assert.Null(await Record.ExceptionAsync(() => view.SelectRowAsync(row)));
            Assert.Contains("ui_details_publication_failed", errors);
            Assert.Empty(queries.Details);
        }
        finally { dispatcher.Fail = false; await view.DisposeAsync(); }
    }

    /// <summary>Terminal dispatcher failure closes read admission while still draining independently admitted desktop actions.</summary>
    [Fact]
    public async Task FailedDetailsPublicationCannotSkipActualActionDrainDuringDispose()
    {
        await using var inner = new DedicatedUiDispatcher();
        var dispatcher = new FailingDispatcher(inner);
        var actions = new BlockingStudentActions();
        var errors = new List<string>();
        var view = new CourseDetailsViewModel(new SearchQueries(), dispatcher, actions, errors.Add);
        var action = view.OpenRegistrationCommand.ExecuteAsync();
        await actions.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.Fail = true;
        var key = new SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "0");
        _ = await Record.ExceptionAsync(() => view.LoadAsync(key));
        var stopped = view.DisposeAsync().AsTask();
        try
        {
            Assert.False(stopped.IsCompleted);
            dispatcher.Fail = false;
            actions.Release.SetResult();
            await action; await stopped;
            Assert.Contains("ui_details_publication_failed", errors);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => view.LoadAsync(key));
        }
        finally
        {
            dispatcher.Fail = false; actions.Release.TrySetResult();
            await action;
            try { await stopped; } catch (Exception) { }
        }
    }

    /// <summary>Escape cannot leave an invalidated details read displaying Loading indefinitely.</summary>
    [Fact]
    public async Task CancelSearchPublishesCanceledDetailsRatherThanSuppressingItsOnlyTerminalState()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queries.DetailsHandler = async (_, token) =>
        { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        await view.SearchCommand.ExecuteAsync();
        var selecting = view.SelectRowAsync(Assert.Single(view.Model.State.Rows).Row);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await view.CancelCommand.ExecuteAsync();
        await selecting;
        Assert.Equal(CourseUiLoadState.Canceled, view.Details.Model.State.Status);
        Assert.Null(view.Details.Model.State.Read);
    }

    /// <summary>An old row callback cannot restart details after filters invalidate its confirmed result.</summary>
    [Fact]
    public async Task FilterChangeBetweenRowResolutionAndCallerContinuationCannotReopenOldDetails()
    {
        await using var inner = new DedicatedUiDispatcher();
        var dispatcher = new ReturnBarrierDispatcher(inner);
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await CourseSearchViewModelTests.Prepare(view, inner);
        await view.SearchCommand.ExecuteAsync();
        var row = Assert.Single(view.Model.State.Rows).Row;
        dispatcher.DelayNextReturn();
        var selecting = view.SelectRowAsync(row);
        try
        {
            await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await inner.InvokeAsync(() => view.CourseCode = "F33.107");
            dispatcher.Release.SetResult();
            await selecting;
            Assert.Null(view.Details.Model.State.Key);
            Assert.Empty(view.Model.State.Rows);
        }
        finally { dispatcher.Release.TrySetResult(); await selecting; }
    }

    /// <summary>Disposal owns an uncooperative actual metadata request rather than its canceled waiter.</summary>
    [Fact]
    public async Task DetailsClearAndDisposeRetainActualUncooperativeReadWithoutPublishingIt()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        var ordinary = new SearchQueries();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = new SnuWatcher.Core.SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "0");
        queries.DetailsHandler = async (identity, _) => { started.SetResult(); await release.Task; return await ordinary.GetDetailsAsync(identity); };
        var view = new CourseDetailsViewModel(queries, dispatcher);
        var reading = view.LoadAsync(key);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatcher.InvokeAsync(view.Clear);
            var stopped = view.DisposeAsync().AsTask();
            Assert.False(stopped.IsCompleted);
            release.SetResult();
            await reading; await stopped;
            Assert.Null(view.Model.State.Key);
            Assert.Null(view.Model.State.Read);
        }
        finally { release.TrySetResult(); await reading; await view.DisposeAsync(); }
    }

    /// <summary>Repeated cancellation cannot replace a still-running callback's ownership receipt with a completed task.</summary>
    [Fact]
    public async Task DetailsRepeatedCancelAndDisposeAwaitTheOriginalCancellationCallbacks()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        var ordinary = new SearchQueries();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacksStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacksRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var key = new SnuWatcher.Core.SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "0");
        queries.DetailsHandler = async (identity, token) =>
        {
            registration = token.Register(() => { callbacksStarted.SetResult(); callbacksRelease.Task.GetAwaiter().GetResult(); });
            started.SetResult(); await actualRelease.Task;
            return await ordinary.GetDetailsAsync(identity);
        };
        var view = new CourseDetailsViewModel(queries, dispatcher);
        var reading = view.LoadAsync(key);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatcher.InvokeAsync(view.Clear);
            await callbacksStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = view.DisposeAsync().AsTask();
            actualRelease.SetResult();
            var winner = await Task.WhenAny(stopping, Task.Delay(100));
            Assert.NotSame(stopping, winner);
            callbacksRelease.SetResult();
            await reading; await stopping;
        }
        finally
        {
            actualRelease.TrySetResult(); callbacksRelease.TrySetResult();
            await registration.DisposeAsync();
            await view.DisposeAsync();
        }
    }

    private sealed class ReturnBarrierDispatcher(IUiDispatcher inner) : IUiDispatcher
    {
        private int delay;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void DelayNextReturn() => Interlocked.Exchange(ref delay, 1);
        public bool CheckAccess() => inner.CheckAccess();
        public void Post(Action action) => inner.Post(action);
        public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            var wait = Interlocked.Exchange(ref delay, 0) == 1;
            await inner.InvokeAsync(action, cancellationToken);
            if (wait) { Entered.SetResult(); await Release.Task; }
        }
    }

    private sealed class FailingDispatcher(IUiDispatcher inner) : IUiDispatcher
    {
        internal bool Fail { get; set; }
        public bool CheckAccess() => inner.CheckAccess();
        public void Post(Action action) { if (Fail) throw new IOException("private UI failure"); inner.Post(action); }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => Fail
            ? Task.FromException(new IOException("private UI failure")) : inner.InvokeAsync(action, cancellationToken);
    }
    private sealed class BlockingStudentActions : IStudentActions
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task OpenDestinationAsync(StudentDestination destination, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); await Release.Task; }
        public Task CopySectionIdentifierAsync(SectionKey key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
