using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Navigation;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Tests.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Search;

/// <summary>Failure/validation rendering and fixed desktop actions, without source or OS access.</summary>
public sealed class CourseSearchFailureTests
{
    /// <summary>Numeric text with unknown quota semantics stays visible without an invented seat count.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Other category")]
    public void UnknownQuotaMeaningCannotDisplayAnOpening(string? quotaLabel)
    {
        var key = new SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "0");
        var item = new CourseSearchItem(new(key, "Course", null, null, null, 116, 120, 130, quotaLabel, SearchQueries.Now), false);
        Assert.Equal("116 / 120", item.EnrollmentText);
        Assert.Null(item.ApparentSeats);
    }

    /// <summary>Both verified source labels use Core's seat policy without altering source text or observation time.</summary>
    [Theory]
    [InlineData("Enrollment/Quota")]
    [InlineData("수강신청인원/정원(재학생)")]
    public void VerifiedQuotaMeaningDisplaysTheApparentOpening(string quotaLabel)
    {
        var key = new SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "0");
        var row = new CourseSearchRow(key, "Course", null, null, null, 116, 120, 130, quotaLabel, SearchQueries.Now);
        var item = new CourseSearchItem(row, false);
        Assert.Equal(4, item.ApparentSeats);
        Assert.Equal(quotaLabel, item.Row.QuotaLabel);
        Assert.Equal(SearchQueries.Now, item.Row.ObservedAtUtc);
    }

    /// <summary>Every explicit result replacement invalidates previous details without relying on a WPF event.</summary>
    [Fact]
    public async Task RepeatingSearchWithNoMatchesClearsPreviouslyConfirmedDetails()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        await view.SearchCommand.ExecuteAsync();
        await view.SelectRowAsync(Assert.Single(view.Model.State.Rows).Row);
        Assert.NotNull(view.Details.Model.State.Read);
        queries.SearchHandler = (query, _) => Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete,
            new(query, [], 1), [], SearchQueries.Now));
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(CourseUiLoadState.Empty, view.Model.State.SearchState);
        Assert.Null(view.Details.Model.State.Key);
        Assert.Null(view.Details.Model.State.Read);
    }

    /// <summary>Unconfirmed terms, invalid years and empty filters cannot issue a source search.</summary>
    [Fact]
    public async Task InvalidOrUnconfirmedInputsStayLocalAndNeverManufactureSourceAuthority()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal("Ui.TermUnconfirmed", view.Model.State.IssueKey);
        await dispatcher.InvokeAsync(() => view.SelectedYear = 2012);
        await view.LoadCatalogCommand.ExecuteAsync();
        Assert.Empty(queries.Catalogs);
        Assert.Equal(CourseUiLoadState.Invalid, view.Model.State.CatalogState);
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        await dispatcher.InvokeAsync(() => view.SelectedTerm = new(2026, "UNCONFIRMED", "Fake", null));
        await view.SearchCommand.ExecuteAsync();
        Assert.Empty(queries.Searches);
        await dispatcher.InvokeAsync(() => { view.SelectedTerm = Assert.Single(view.Model.State.Terms); view.CourseCode = ""; });
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal("Ui.FiltersInvalid", view.Model.State.IssueKey);
        Assert.Empty(queries.Searches);
    }

    /// <summary>Unexpected query exceptions are sanitized and source-provided wrong term rows stay unselectable.</summary>
    [Fact]
    public async Task SourceExceptionAndOutOfScopeRowsDoNotBecomeCurrentAvailability()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var errors = new List<string>();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock(), errors.Add);
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        queries.SearchHandler = (_, _) => throw new IOException("private source message");
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(CourseUiLoadState.Failed, view.Model.State.SearchState);
        Assert.Contains("ui_search_failed", errors);
        Assert.DoesNotContain("private", view.Model.State.IssueKey ?? "");
        queries.SearchHandler = (query, _) => Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete,
            new(query, [SearchQueries.Row(new(2026, "U000200002", "OTHER", "F32.102", "012", "0"))], 1), [], SearchQueries.Now));
        await view.SearchCommand.ExecuteAsync();
        Assert.Equal(CourseUiLoadState.Partial, view.Model.State.SearchState);
        Assert.Empty(view.Model.State.Rows);
        await view.SelectRowAsync(SearchQueries.Row(new(2026, "U000200002", "OTHER", "F32.102", "012", "0")));
        Assert.Empty(queries.Details);
    }

    /// <summary>Fixed browser/copy actions retain the full identity and missing counts remain unknown.</summary>
    [Fact]
    public async Task DetailsActionsUseOnlyFixedDestinationsAndTheExactSelectedIdentifier()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var actions = new RecordingStudentActions();
        var queries = new SearchQueries();
        await using var view = new CourseDetailsViewModel(queries, dispatcher, actions);
        Assert.False(view.CopyIdentifierCommand.CanExecute(null));
        await view.OpenRegistrationCommand.ExecuteAsync();
        await view.OpenGenieCommand.ExecuteAsync();
        var key = new SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "SPECIAL");
        await view.LoadAsync(key);
        await view.CopyIdentifierCommand.ExecuteAsync();
        Assert.Equal(new[] { StudentDestination.Registration, StudentDestination.Genie }, actions.Opened);
        Assert.Equal(key, Assert.Single(actions.Copied));
        Assert.Equal(SearchQueries.Now, view.Model.State.Read!.CompletedAtUtc);
        var known = new CourseSearchItem(SearchQueries.Row(key, 121), true);
        Assert.Equal("121 / 120", known.EnrollmentText);
        Assert.Equal(0, known.ApparentSeats);
        var missingRegistered = new CourseSearchItem(new(key, "Course", null, null, null, null, 120, 400, "Quota", SearchQueries.Now), false);
        var missingCapacity = new CourseSearchItem(new(key, "Course", null, null, null, 0, null, 400, "Quota", SearchQueries.Now), false);
        Assert.Null(missingRegistered.ApparentSeats);
        Assert.Null(missingCapacity.ApparentSeats);
        Assert.Equal("— / 120", missingRegistered.EnrollmentText);
        Assert.Equal("0 / —", missingCapacity.EnrollmentText);
    }

    /// <summary>Foreign details and unexpected failures never replace the selected exact metadata.</summary>
    [Fact]
    public async Task DetailsRejectWrongIdentityAndDisplayTypedFailureWithoutRestamping()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        var ordinary = new SearchQueries();
        var key = new SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "0");
        queries.DetailsHandler = (_, token) => ordinary.GetDetailsAsync(new(2026, "U000200002", "U000300002", "F33.107", "002", "0"), token);
        await using var view = new CourseDetailsViewModel(queries, dispatcher);
        await view.LoadAsync(key);
        Assert.Equal(CourseUiLoadState.Failed, view.Model.State.Status);
        Assert.Null(view.Model.State.Read);
        queries.DetailsHandler = (_, _) => Task.FromResult(new SourceRead<CourseDetails>(SourceReadStatus.Failed, null,
            [new(SourceProblemKind.Unavailable, "source_unavailable")], SearchQueries.Now));
        await view.LoadAsync(key);
        Assert.Equal(CourseUiLoadState.Failed, view.Model.State.Status);
        Assert.Equal(SearchQueries.Now, view.Model.State.Read!.CompletedAtUtc);
    }

    /// <summary>Selection/college resets remain local and observers receive changes only on the dispatcher.</summary>
    [Fact]
    public async Task LocalFilterChangesAndCancellationPublishOnUiWithoutIssuingRequests()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        await using var view = new CourseSearchViewModel(queries, new SearchWatches(), dispatcher, new SearchClock());
        var threads = new List<int>();
        view.PropertyChanged += (_, _) => threads.Add(Environment.CurrentManagedThreadId);
        await CourseSearchViewModelTests.Prepare(view, dispatcher);
        await view.LoadCatalogCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() =>
        {
            view.SelectedCollege = Assert.Single(view.Model.State.Colleges);
            Assert.Empty(view.Model.State.Departments);
            view.SelectedCollege = null; view.SelectedDepartment = null;
        });
        await view.CancelCommand.ExecuteAsync();
        Assert.Equal(CourseUiLoadState.Canceled, view.Model.State.SearchState);
        Assert.Empty(queries.Searches);
        Assert.All(threads, thread => Assert.Equal(dispatcher.ThreadId, thread));
        Assert.Throws<InvalidOperationException>(() => view.CourseName = "Wrong thread");
    }

    private sealed class RecordingStudentActions : IStudentActions
    {
        internal List<StudentDestination> Opened { get; } = [];
        internal List<SectionKey> Copied { get; } = [];
        public Task OpenDestinationAsync(StudentDestination destination, CancellationToken cancellationToken = default)
        { Opened.Add(destination); return Task.CompletedTask; }
        public Task CopySectionIdentifierAsync(SectionKey key, CancellationToken cancellationToken = default)
        { Copied.Add(key); return Task.CompletedTask; }
    }
}
