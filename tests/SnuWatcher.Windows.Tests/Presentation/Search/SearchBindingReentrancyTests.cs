using SnuWatcher.Application.Sources;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Tests.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Search;

/// <summary>Binding replay is idempotent, while genuine nested user intents remain observable and effective.</summary>
public sealed class SearchBindingReentrancyTests
{
    /// <summary>A genuine edit reentered while clearing old details invalidates the obsolete admission instead of restoring captured filters.</summary>
    [Fact]
    public async Task NestedFilterEditDuringExplicitSearchAdmissionCannotBeOverwrittenOrSent()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        await view.SearchCommand.ExecuteAsync();
        await view.SelectRowAsync(Assert.Single(view.Model.State.Rows).Row);
        var calls = queries.Searches.Count;
        var changed = false;
        view.Details.Model.PropertyChanged += (_, _) =>
        {
            if (changed) return;
            changed = true; view.CourseCode = "F33.107"; view.Instructor = "New instructor intent";
        };
        await view.SearchCommand.ExecuteAsync();
        Assert.True(changed);
        Assert.Equal("F33.107", view.CourseCode);
        Assert.Equal("New instructor intent", view.Instructor);
        Assert.Equal(CourseUiLoadState.Idle, view.Model.State.SearchState);
        Assert.Empty(view.Model.State.Rows);
        Assert.Equal(calls, queries.Searches.Count);
    }

    /// <summary>Reapplying unchanged scalar values and equivalent selected identities must not reset a confirmed result.</summary>
    [Fact]
    public async Task UnchangedBindingReplayPreservesTheExactSnapshotDetailsAndExplicitSelection()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        await view.LoadCatalogCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() => view.SelectedCollege = Assert.Single(view.Model.State.Colleges));
        await view.LoadCatalogCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() => view.SelectedDepartment = Assert.Single(view.Model.State.Departments));
        await view.SearchCommand.ExecuteAsync();
        var row = Assert.Single(view.Model.State.Rows).Row;
        await dispatcher.InvokeAsync(() => view.ToggleSection(row.Key, true));
        await view.SelectRowAsync(row);
        var original = view.Model.State;
        var details = view.Details.Model.State;
        var selection = view.WatchSelection.Model.State;
        var notifications = 0;
        view.PropertyChanged += (_, _) => notifications++;
        await dispatcher.InvokeAsync(() =>
        {
            view.SelectedYear = original.SelectedYear;
            view.SelectedTerm = new(original.SelectedTerm!.Year, original.SelectedTerm.Selector, "Equivalent identity", null);
            view.SelectedCollege = new(original.SelectedCollege!.Code, "Equivalent identity", null, "UP_DEPT");
            view.SelectedDepartment = new(original.SelectedDepartment!.Code, "Equivalent identity", null, "DEPT");
            view.CourseCode = original.CourseCode; view.CourseName = null!; view.Instructor = original.Instructor; view.SectionLookup = original.SectionLookup;
        });
        Assert.Same(original, view.Model.State);
        Assert.Same(details, view.Details.Model.State);
        Assert.Same(selection, view.WatchSelection.Model.State);
        Assert.Equal(0, notifications);
        Assert.Single(queries.Searches);
    }

    /// <summary>A finite unit observer reproduces the WPF setter echo without allowing a process-killing stack overflow.</summary>
    [Fact]
    public async Task ReentrantBindingEchoConvergesWhenAGenuineYearChangeClearsOptions()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        await using var view = new CourseSearchViewModel(new SearchQueries(), new SearchWatches(), dispatcher, new SearchClock());
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        var echoes = 0;
        var exceededBound = false;
        view.PropertyChanged += (_, _) =>
        {
            if (++echoes > 12) { exceededBound = true; return; }
            view.SelectedYear = view.SelectedYear;
            view.SelectedTerm = view.SelectedTerm;
            view.SelectedCollege = view.SelectedCollege;
            view.SelectedDepartment = view.SelectedDepartment;
        };
        await dispatcher.InvokeAsync(() => view.SelectedYear = 2027);
        Assert.False(exceededBound);
        Assert.Equal(1, echoes);
        Assert.Equal(2027, view.SelectedYear);
        Assert.Null(view.SelectedTerm);
        Assert.Empty(view.Model.State.Terms);
    }

    /// <summary>Unchanged input while a request is active must not cancel its token or invalidate its eventual rows.</summary>
    [Fact]
    public async Task SameInputDuringAnActiveSearchDoesNotCancelTheActualRequest()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        var release = new TaskCompletionSource<SourceRead<CourseSearchData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        queries.SearchHandler = (_, token) => { requestToken = token; return release.Task; };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        var running = view.SearchCommand.ExecuteAsync();
        try
        {
            await queries.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatcher.InvokeAsync(() => view.CourseCode = view.CourseCode);
            Assert.False(requestToken.IsCancellationRequested);
            release.SetResult(SearchQueries.Result(Assert.Single(queries.Searches)));
            await running;
            Assert.Equal(CourseUiLoadState.Complete, view.Model.State.SearchState);
            Assert.Single(view.Model.State.Rows);
        }
        finally { release.TrySetResult(SearchQueries.Result(new(2026, SearchQueries.Selector, "F32.102"))); await running; }
    }

    /// <summary>Reentrant honest edits survive; child cleanup observes the already-committed parent filter transition.</summary>
    [Fact]
    public async Task GenuineNestedEditIsPreservedAndChildObserversSeeTheNewFilterState()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        await using var view = new CourseSearchViewModel(new SearchQueries(), new SearchWatches(), dispatcher, new SearchClock());
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        var nested = false;
        string? observedCode = null;
        view.Details.Model.PropertyChanged += (_, _) =>
        {
            if (nested) return;
            nested = true; observedCode = view.CourseCode; view.Instructor = "New instructor intent";
        };
        await dispatcher.InvokeAsync(() => view.CourseCode = "F33.107");
        Assert.Equal("F33.107", observedCode);
        Assert.Equal("F33.107", view.CourseCode);
        Assert.Equal("New instructor intent", view.Instructor);
    }
}
