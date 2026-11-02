using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Tests.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Search;

/// <summary>Explicit search, source confirmation and dependent-filter transitions.</summary>
public sealed class CourseSearchViewModelTests
{
    /// <summary>Typing has no transport side effect and verified filters remain exact.</summary>
    [Fact]
    public async Task EverySupportedFilterUsesConfirmedSelectorsAndTypingSendsNothing()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await dispatcher.InvokeAsync(() => view.SelectedYear = 2026);
        Assert.Empty(queries.Catalogs);
        await view.LoadCatalogCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() => view.SelectedTerm = Assert.Single(view.Model.State.Terms));
        await view.LoadCatalogCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() => view.SelectedCollege = Assert.Single(view.Model.State.Colleges));
        await view.LoadCatalogCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() =>
        {
            view.SelectedDepartment = Assert.Single(view.Model.State.Departments);
            view.CourseCode = "F32.102"; view.CourseName = "통계학"; view.Instructor = "이권상";
        });
        Assert.Empty(queries.Searches);
        await view.SearchCommand.ExecuteAsync();
        var query = Assert.Single(queries.Searches);
        Assert.Equal(new CourseSearchQuery(2026, SearchQueries.Selector, "F32.102", "통계학", "이권상", "COLLEGE", "DEPT"), query);
        Assert.Equal(CourseUiLoadState.Complete, view.Model.State.SearchState);
        Assert.Equal(SearchQueries.Now, Assert.Single(view.Model.State.Rows).Row.ObservedAtUtc);
    }

    /// <summary>A newer year invalidates late uncooperative search completion.</summary>
    [Fact]
    public async Task ChangingYearCancelsAndDiscardsAnUncooperativeOldSearchAndClearsDependentSelections()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        var release = new TaskCompletionSource<SourceRead<CourseSearchData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        queries.SearchHandler = (_, _) => release.Task;
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await Prepare(view, dispatcher);
        var running = view.SearchCommand.ExecuteAsync();
        try
        {
            await queries.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatcher.InvokeAsync(() => view.SelectedYear = 2027);
            release.SetResult(SearchQueries.Result(queries.Searches[0]));
            await running;
            Assert.Equal(2027, view.SelectedYear);
            Assert.Null(view.SelectedTerm);
            Assert.Empty(view.Model.State.Terms);
            Assert.Empty(view.Model.State.Rows);
            Assert.Empty(view.WatchSelection.Model.State.Rows);
            Assert.Null(view.Details.Model.State.Key);
        }
        finally { release.TrySetResult(SearchQueries.Result(new(2026, SearchQueries.Selector, "F32.102"))); await running; }
    }

    /// <summary>Partial absence remains unknown and failed reads remain visibly failed.</summary>
    [Fact]
    public async Task PartialAndFailedSearchesNeverConvertMissingRowsIntoKnownFullOrCompleteEmpty()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await Prepare(view, dispatcher);
        queries.SearchHandler = (query, _) => Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Partial,
            new(query, [], 1), [new(SourceProblemKind.RateLimited, "source_rate_limited")], SearchQueries.Now));
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(CourseUiLoadState.Partial, view.Model.State.SearchState);
        Assert.Empty(view.Model.State.Rows);
        queries.SearchHandler = (_, _) => Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Failed,
            null, [new(SourceProblemKind.Unavailable, "source_unavailable")], SearchQueries.Now));
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(CourseUiLoadState.Failed, view.Model.State.SearchState);
        Assert.Empty(view.Model.State.Rows);
    }

    /// <summary>Literal lookup resolves a full verified row and never trusts caller-provided counts.</summary>
    [Fact]
    public async Task ExactSectionLookupRequiresThreeAsciiDigitsAndAnActualCurrentCatalogConfirmedRow()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await Prepare(view, dispatcher);
        await dispatcher.InvokeAsync(() => view.SectionLookup = "12");
        await view.SearchCommand.ExecuteAsync();
        Assert.Empty(queries.Searches);
        await dispatcher.InvokeAsync(() => view.SectionLookup = "012");
        await view.SearchCommand.ExecuteAsync();
        var row = Assert.Single(view.Model.State.Rows).Row;
        var forged = SearchQueries.Row(row.Key, 0);
        await dispatcher.InvokeAsync(() => view.ToggleSection(forged.Key, true));
        Assert.Equal(120, Assert.Single(view.WatchSelection.Model.State.Rows).Registered);
        Assert.Equal("2학기", view.WatchSelection.Model.State.TermName);
        Assert.Equal(2026, view.WatchSelection.Model.State.Year);
        await view.SelectRowAsync(row);
        Assert.Equal("2학기", view.Details.Model.State.TermName);
        await dispatcher.InvokeAsync(() => view.ToggleSection(new(2026, "U000200002", "U000300002", "FORGED", "012", "0"), true));
        Assert.Single(view.WatchSelection.Model.State.Rows);
        await dispatcher.InvokeAsync(() => view.SectionLookup = "999");
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(CourseUiLoadState.Empty, view.Model.State.SearchState);
        Assert.Empty(view.Model.State.Rows);
    }

    /// <summary>Clear is local and removes stale dependent selection.</summary>
    [Fact]
    public async Task ClearRemovesDetailsSelectionAndFiltersWithoutCreatingARequest()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await Prepare(view, dispatcher);
        await view.SearchCommand.ExecuteAsync();
        var row = Assert.Single(view.Model.State.Rows).Row;
        await dispatcher.InvokeAsync(() => view.ToggleSection(row.Key, true));
        await view.SelectRowAsync(row);
        var calls = queries.Searches.Count;
        await view.ClearCommand.ExecuteAsync();
        Assert.Equal(calls, queries.Searches.Count);
        Assert.Equal("", view.CourseCode);
        Assert.Empty(view.Model.State.Rows);
        Assert.Empty(view.WatchSelection.Model.State.Rows);
        Assert.Null(view.WatchSelection.Model.State.Year);
        Assert.Null(view.Details.Model.State.Key);
    }

    internal static async Task Prepare(CourseSearchViewModel view, DedicatedUiDispatcher dispatcher)
    {
        await dispatcher.InvokeAsync(() => view.SelectedYear = 2026);
        await view.LoadCatalogCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() => { view.SelectedTerm = Assert.Single(view.Model.State.Terms); view.CourseCode = "F32.102"; });
    }
}

internal sealed class SearchClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => SearchQueries.Now;
}

internal sealed class SearchQueries : IRuntimeCourseQueries
{
    internal const string Selector = "U000200002U000300002";
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T01:00:00Z");
    internal List<CatalogQuery> Catalogs { get; } = [];
    internal List<CourseSearchQuery> Searches { get; } = [];
    internal List<SectionKey> Details { get; } = [];
    internal TaskCompletionSource SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<CourseSearchQuery, CancellationToken, Task<SourceRead<CourseSearchData>>>? SearchHandler { get; set; }
    internal Func<SectionKey, CancellationToken, Task<SourceRead<CourseDetails>>>? DetailsHandler { get; set; }
    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, CancellationToken cancellationToken = default)
    {
        Catalogs.Add(query);
        return Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
            new(query, [new(query.Year, Selector, "2학기", "Second semester")],
                [new("COLLEGE", "단과대학", null, "UP_DEPT"), new("DEPT", "통계학과", null, "DEPT")]), [], Now));
    }
    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, CancellationToken cancellationToken = default)
    {
        Searches.Add(query); SearchStarted.TrySetResult();
        return SearchHandler?.Invoke(query, cancellationToken) ?? Task.FromResult(Result(query));
    }
    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, CancellationToken cancellationToken = default)
    {
        Details.Add(key);
        return DetailsHandler?.Invoke(key, cancellationToken) ?? Task.FromResult(new SourceRead<CourseDetails>(SourceReadStatus.Complete,
            new(key, "통계학", "Statistics", "이권상", null, "통계학과", 3, 120, "Korean", null, [new("월 14:00", null, "25-101")]), [], Now));
    }
    internal static SourceRead<CourseSearchData> Result(CourseSearchQuery query) => new(SourceReadStatus.Complete,
        new(query, [Row(new(query.Year, "U000200002", "U000300002", query.CourseCode ?? "F32.102", "012", "0"))], 1), [], Now);
    internal static CourseSearchRow Row(SectionKey key, int registered = 120) => new(key, "통계학", "이권상", "통계학과", "월 14:00", registered, 120, 123, "수강신청인원/정원(재학생)", Now);
}

internal sealed class SearchWatches : IRuntimeWatchCommands
{
    internal List<NewSubscription> Creates { get; } = [];
    internal Func<NewSubscription, CancellationToken, Task<SubscriptionSnapshot>>? CreateHandler { get; set; }
    public Task<SubscriptionSnapshot> CreateAsync(NewSubscription command, CancellationToken cancellationToken = default)
    { Creates.Add(command); return CreateHandler?.Invoke(command, cancellationToken) ?? Task.FromResult(Snapshot(command)); }
    public Task<SubscriptionSnapshot> ChangeAsync(SubscriptionChange command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OwnedRefreshResult> RefreshAsync(SectionKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    internal static SubscriptionSnapshot Snapshot(NewSubscription command) => new(command.Id, 1, 1, command.Kind, command.Label,
        WatchStatus.Active, command.InitialNoticeEnabled, command.Expiry, command.CreatedAtUtc, command.Sections.Select(key => new MemberSnapshot(key, 0, null)));
}
