using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Tests.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Search;

/// <summary>The site's current term, an empty search that lists every course, and one-page-at-a-time paging.</summary>
public sealed class CourseSearchPagingTests
{
    /// <summary>The current term is read once, selected with its colleges, and never runs a search.</summary>
    [Fact]
    public async Task CurrentTermIsReadOnceSelectedWithItsCollegesAndNeverSearches()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries { CatalogHandler = (query, _) => Task.FromResult(Catalog(query, 2026)) };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        Assert.Equal(UiCommandOutcome.Completed, await view.LoadCurrentTermCommand.ExecuteAsync());
        var state = view.Model.State;
        Assert.Equal((2026, SearchQueries.Selector), (state.SelectedTerm!.Year, state.SelectedTerm.Selector));
        Assert.Equal(2026, state.SelectedYear);
        Assert.Equal(CourseUiLoadState.Complete, state.CatalogState);
        Assert.Equal("COLLEGE", Assert.Single(state.Colleges).Code);
        Assert.Empty(state.Departments);
        await view.LoadCurrentTermCommand.ExecuteAsync();
        Assert.Equal(new CatalogQuery(2026), Assert.Single(queries.Catalogs));
        Assert.Empty(queries.Searches);
    }

    /// <summary>In January the site's current term can belong to the previous year; that year's terms are then read.</summary>
    [Fact]
    public async Task ACurrentTermFromAnotherYearReadsThatYearsTerms()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries { CatalogHandler = (query, _) => Task.FromResult(Catalog(query, 2026)) };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher,
            new FixedClock(DateTimeOffset.Parse("2027-01-10T01:00:00Z")));
        await view.LoadCurrentTermCommand.ExecuteAsync();
        Assert.Equal(new[] { new CatalogQuery(2027), new CatalogQuery(2026) }, queries.Catalogs.ToArray());
        Assert.Equal(2026, view.Model.State.SelectedTerm!.Year);
        Assert.Equal(2026, view.Model.State.SelectedYear);
    }

    /// <summary>An unknown or unreadable current term stays visibly unavailable and can be retried.</summary>
    [Fact]
    public async Task AnUnknownOrFailedCurrentTermIsReportedAndRetriedBySearch()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var reports = new List<string>();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock(), reports.Add);
        await view.LoadCurrentTermCommand.ExecuteAsync();
        Assert.Null(view.Model.State.SelectedTerm);
        Assert.Equal((CourseUiLoadState.Failed, "Ui.CurrentTermUnavailable"), (view.Model.State.CatalogState, view.Model.State.IssueKey));
        queries.CatalogHandler = (_, _) => throw new InvalidOperationException("transport");
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(2, queries.Catalogs.Count);
        Assert.Empty(queries.Searches);
        Assert.Equal(CourseUiLoadState.Invalid, view.Model.State.SearchState);
        Assert.Contains("ui_current_term_failed", reports);
        queries.CatalogHandler = (query, _) => Task.FromResult(Catalog(query, 2026));
        await view.LoadCurrentTermCommand.ExecuteAsync();
        Assert.NotNull(view.Model.State.SelectedTerm);
        Assert.Null(view.Model.State.IssueKey);
    }

    /// <summary>Canceling the term read, directly or through a search waiting on it, leaves it unselected and retryable.</summary>
    [Fact]
    public async Task CancelingTheCurrentTermReadLeavesItCanceledAndRetryable()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queries.CatalogHandler = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        var loading = view.LoadCurrentTermCommand.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.LoadCurrentTermCommand.Cancel();
        Assert.Equal(UiCommandOutcome.Canceled, await loading);
        Assert.Equal(CourseUiLoadState.Canceled, view.Model.State.CatalogState);

        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var searching = view.SearchCommand.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.SearchCommand.Cancel();
        await searching;
        Assert.Equal(CourseUiLoadState.Canceled, view.Model.State.SearchState);
        Assert.Empty(queries.Searches);
    }

    /// <summary>Cancel stops the automatic term read; a response arriving after Cancel never selects a term.</summary>
    [Fact]
    public async Task CancelStopsTheCurrentTermReadAndALateResponseCannotOverwriteIt()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SourceRead<CourseCatalogData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new SearchQueries { CatalogHandler = (_, _) => { entered.TrySetResult(); return release.Task; } };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        var loading = view.LoadCurrentTermCommand.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await view.CancelCommand.ExecuteAsync();
        // The fake ignores cancellation, as a slow source might: its answer still arrives after Cancel.
        release.SetResult(Catalog(new(2026), 2026));
        Assert.Equal(UiCommandOutcome.Canceled, await loading);
        Assert.Null(view.Model.State.SelectedTerm);
        Assert.Equal(CourseUiLoadState.Canceled, view.Model.State.CatalogState);

        // Editing a filter never cancels the term read; only Cancel does.
        queries.CatalogHandler = (query, _) => Task.FromResult(Catalog(query, 2026));
        await dispatcher.InvokeAsync(() => view.CourseName = "통계");
        Assert.Equal(UiCommandOutcome.Completed, await view.LoadCurrentTermCommand.ExecuteAsync());
        Assert.NotNull(view.Model.State.SelectedTerm);
    }

    /// <summary>An empty search lists the term one page at a time; paging keeps the searched filters.</summary>
    [Fact]
    public async Task AnEmptySearchListsEveryCoursePageByPageAndPagingKeepsTheSearchedFilters()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries
        {
            CatalogHandler = (query, _) => Task.FromResult(Catalog(query, 2026)),
            SearchHandler = (query, _) => Task.FromResult(Paged(query, total: 279, lastPage: 28)),
        };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        Assert.False(view.PageCommand.CanExecute(null));
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(new CourseSearchQuery(2026, SearchQueries.Selector, page: 1), Assert.Single(queries.Searches));
        var state = view.Model.State;
        Assert.Equal((1, 28, 279, true), (state.CurrentPage, state.LastPage, state.ResultCount, state.IsPaged));
        Assert.Equal((false, true), (state.HasPreviousPage, state.HasNextPage));
        Assert.Equal(new SearchPageLink[] { new(1, true), new(2, false), new(3, false), new(4, false), new(5, false) }, state.PageNumbers.ToArray());

        // Editing a filter after the search never changes what the next page belongs to: editing clears the listing.
        await dispatcher.InvokeAsync(() => view.Grade = "2");
        Assert.False(view.Model.State.IsPaged);
        await dispatcher.InvokeAsync(() => view.Grade = "");
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(UiCommandOutcome.Completed, await GoTo(dispatcher, view, 7));
        Assert.Equal(new CourseSearchQuery(2026, SearchQueries.Selector, page: 7), queries.Searches[^1]);
        state = view.Model.State;
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, state.PageNumbers.Select(link => link.Number).ToArray());
        Assert.True(state.PageNumbers.Single(link => link.Number == 7).IsCurrent);
        Assert.Equal((true, true), (state.HasPreviousPage, state.HasNextPage));

        var calls = queries.Searches.Count;
        foreach (var page in new[] { 0, 7, 29 })
            Assert.Equal(UiCommandOutcome.Unavailable, await GoTo(dispatcher, view, page));
        Assert.Equal(calls, queries.Searches.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => view.GoToPageAsync(2));
    }

    /// <summary>The last block shows only the pages that exist, and a listing without stated totals still pages safely.</summary>
    [Fact]
    public async Task TheLastBlockShowsExistingPagesAndUnstatedTotalsFallBackToTheShownRows()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries
        {
            CatalogHandler = (query, _) => Task.FromResult(Catalog(query, 2026)),
            SearchHandler = (query, _) => Task.FromResult(Paged(query, total: 279, lastPage: 28)),
        };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await view.SearchCommand.ExecuteAsync();
        await GoTo(dispatcher, view, 28);
        Assert.Equal(new[] { 26, 27, 28 }, view.Model.State.PageNumbers.Select(link => link.Number).ToArray());
        Assert.False(view.Model.State.HasNextPage);

        queries.SearchHandler = (query, _) => Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete,
            new(query, [SearchQueries.Row(new(2026, "U000200002", "U000300002", "F32.102", "001", "0"))], 1), [], SearchQueries.Now));
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal((1, 1, 1), (view.Model.State.CurrentPage, view.Model.State.LastPage, view.Model.State.ResultCount));
        Assert.Equal(new SearchPageLink[] { new(1, true) }, view.Model.State.PageNumbers.ToArray());

        // A section lookup reads every page of its course code, so it has no paging bar.
        await dispatcher.InvokeAsync(() => { view.CourseCode = "F32.102"; view.SectionLookup = "001"; });
        await view.SearchCommand.ExecuteAsync();
        Assert.Null(queries.Searches[^1].Page);
        Assert.Equal((null, null, false), (view.Model.State.CurrentPage, view.Model.State.LastPage, view.Model.State.IsPaged));
        Assert.Empty(view.Model.State.PageNumbers);
    }

    /// <summary>The grade and degree-program filters are sent as the site's own option codes.</summary>
    [Fact]
    public async Task GradeAndProgramFiltersAreSentUnchanged()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries { CatalogHandler = (query, _) => Task.FromResult(Catalog(query, 2026)) };
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await view.LoadCurrentTermCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() => { view.Grade = "3"; view.Level = "C013300001"; view.Grade = "3"; view.Level = null!; view.Level = "C013300001"; });
        Assert.Equal(("3", "C013300001"), (view.Grade, view.Level));
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(new CourseSearchQuery(2026, SearchQueries.Selector, page: 1, grade: "3", level: "C013300001"), Assert.Single(queries.Searches));
        await dispatcher.InvokeAsync(() => view.Grade = null!);
        Assert.Equal("", view.Grade);
    }

    private static SourceRead<CourseCatalogData> Catalog(CatalogQuery query, int currentYear) => new(SourceReadStatus.Complete,
        new CourseCatalogData(query, [new(query.Year, SearchQueries.Selector, "겨울학기", "Winter Semester")],
            [new("COLLEGE", "단과대학", null, "UP_DEPT"), new("DEPT", "통계학과", null, "DEPT")])
        { CurrentYear = currentYear, CurrentTermSelector = SearchQueries.Selector }, [], SearchQueries.Now);

    private static SourceRead<CourseSearchData> Paged(CourseSearchQuery query, int total, int lastPage) => new(SourceReadStatus.Complete,
        new(query, [SearchQueries.Row(new(query.Year, "U000200002", "U000300002", "F32.102", $"{query.Page:000}", "0"))], 1, total, lastPage),
        [], SearchQueries.Now);

    private static async Task<UiCommandOutcome> GoTo(DedicatedUiDispatcher dispatcher, CourseSearchViewModel view, int page)
    {
        Task<UiCommandOutcome>? paging = null;
        await dispatcher.InvokeAsync(() => paging = view.GoToPageAsync(page));
        return await paging!;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
