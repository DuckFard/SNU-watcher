using System.Collections.Immutable;
using SnuWatcher.Application.Sources;

namespace SnuWatcher.Windows.Presentation.Search;

public sealed partial class CourseSearchViewModel
{
    private async Task LoadCatalogAsync(CancellationToken cancellationToken)
    {
        CatalogQuery? query = null;
        long version = 0;
        await dispatcher.InvokeAsync(() =>
        {
            version = generation;
            try
            {
                var state = Model.State;
                if (state.SelectedYear is not { } year) throw new ArgumentException("Select a year.");
                query = new(year, state.SelectedTerm?.Selector, state.SelectedCollege?.Code);
                Model.Replace(state with { CatalogState = CourseUiLoadState.Loading, IssueKey = null });
            }
            catch (ArgumentException) { Model.Replace(Model.State with { CatalogState = CourseUiLoadState.Invalid, IssueKey = "Ui.FiltersInvalid" }); }
        }, cancellationToken).ConfigureAwait(false);
        if (query is null) return;
        try
        {
            var read = await queries.GetCatalogAsync(query, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read.Data is { } catalog && catalog.Query != query) throw new InvalidOperationException("Unexpected catalog scope.");
            await PublishAsync(version, state =>
            {
                if (read.Data is null) return state with { CatalogRead = read, CatalogState = CourseUiLoadState.Failed };
                var colleges = read.Data.Departments.Where(option => option.Level == "UP_DEPT").ToImmutableArray();
                return state with
                {
                    CatalogRead = read, CatalogState = CourseUiLoadState.Complete, Terms = read.Data.Terms,
                    Colleges = colleges.IsEmpty && state.SelectedCollege is not null ? state.Colleges : colleges,
                    Departments = read.Data.Departments.Where(option => option.Level == "DEPT").ToImmutableArray(),
                };
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await PublishAsync(version, state => state with { CatalogState = CourseUiLoadState.Canceled }).ConfigureAwait(false); }
        catch (Exception)
        {
            await PublishAsync(version, state => state with { CatalogState = CourseUiLoadState.Failed, IssueKey = "Ui.DataUnavailable" }).ConfigureAwait(false);
            Report("ui_catalog_failed");
        }
    }

    /// <summary>
    /// Selects the term the source reports as current. The first read asks for the calendar year; the site
    /// answers with its own current term, which can belong to another year (its winter term in January), and
    /// that year's term list is then read once. Filter edits during the read never discard its result.
    /// </summary>
    private async Task LoadCurrentTermAsync(CancellationToken cancellationToken)
    {
        var start = false;
        await dispatcher.InvokeAsync(() =>
        {
            var state = Model.State;
            if (state.SelectedTerm is not null || state.CatalogState == CourseUiLoadState.Loading) return;
            start = true;
            Model.Replace(state with { CatalogState = CourseUiLoadState.Loading });
        }, cancellationToken).ConfigureAwait(false);
        if (start) await ReadCurrentTermAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReadCurrentTermAsync(CancellationToken cancellationToken)
    {
        try
        {
            var year = Math.Clamp(clock.GetUtcNow().Year, 2013, 2200);
            var read = await queries.GetCatalogAsync(new(year), cancellationToken).ConfigureAwait(false);
            if (read.Data is { CurrentTerm: null, CurrentYear: { } reported } && reported != year)
                read = await queries.GetCatalogAsync(new(reported), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var catalog = read.Data;
            var term = catalog?.CurrentTerm;
            var selected = false;
            await dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                var state = Model.State;
                if (state.SelectedTerm is not null) { selected = true; return; }
                if (catalog is null || term is null)
                {
                    Model.Replace(state with { CatalogRead = read, CatalogState = CourseUiLoadState.Failed, IssueKey = "Ui.CurrentTermUnavailable" });
                    return;
                }
                selected = true;
                Model.Replace(state with
                {
                    SelectedYear = term.Year, SelectedTerm = term, Terms = catalog.Terms, CatalogRead = read,
                    CatalogState = CourseUiLoadState.Complete, SelectedCollege = null, SelectedDepartment = null,
                    Colleges = catalog.Departments.Where(option => option.Level == "UP_DEPT").ToImmutableArray(), Departments = [],
                    IssueKey = state.IssueKey == "Ui.CurrentTermUnavailable" ? null : state.IssueKey,
                });
            }).ConfigureAwait(false);
            return selected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (!disposed && Model.State.SelectedTerm is null) Model.Replace(Model.State with { CatalogState = CourseUiLoadState.Canceled });
            }).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (!disposed && Model.State.SelectedTerm is null)
                    Model.Replace(Model.State with { CatalogState = CourseUiLoadState.Failed, IssueKey = "Ui.CurrentTermUnavailable" });
            }).ConfigureAwait(false);
            Report("ui_current_term_failed");
            return false;
        }
    }

    private async Task SearchAsync(int? page, CancellationToken cancellationToken)
    {
        // A search before the current term is known reads it first, so the button always does what it says.
        var needsTerm = false;
        await dispatcher.InvokeAsync(() =>
        {
            needsTerm = page is null && Model.State.SelectedTerm is null;
            if (needsTerm) Model.Replace(Model.State with { CatalogState = CourseUiLoadState.Loading, SearchState = CourseUiLoadState.Loading, IssueKey = null });
        }, cancellationToken).ConfigureAwait(false);
        if (needsTerm)
        {
            var known = false;
            try { known = await ReadCurrentTermAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            if (!known)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (!disposed && Model.State.SearchState == CourseUiLoadState.Loading)
                        Model.Replace(Model.State with { SearchState = cancellationToken.IsCancellationRequested ? CourseUiLoadState.Canceled : CourseUiLoadState.Invalid });
                }).ConfigureAwait(false);
                return;
            }
        }
        CourseSearchQuery? query = null;
        string lookup = "";
        long version = 0;
        await dispatcher.InvokeAsync(() =>
        {
            version = generation;
            var state = Model.State;
            try
            {
                if (page is { } next)
                {
                    // Paging keeps the filters the displayed results were searched with.
                    if (!state.IsPaged || state.SearchRead?.Data?.Query is not { } shown) return;
                    query = shown.ForPage(next);
                }
                else
                {
                    if (state.SelectedYear is not { } year || state.SelectedTerm is not { } term
                        || !state.Terms.Any(option => option.Year == year && option.Selector == term.Selector))
                    { Model.Replace(state with { SearchState = CourseUiLoadState.Invalid, IssueKey = "Ui.TermUnconfirmed" }); return; }
                    lookup = state.SectionLookup.Trim();
                    if (lookup.Length > 0 && (lookup.Length != 3 || lookup.Any(character => character is < '0' or > '9') || string.IsNullOrWhiteSpace(state.CourseCode)))
                    { Model.Replace(state with { SearchState = CourseUiLoadState.Invalid, IssueKey = "Ui.InvalidSection" }); return; }
                    // A section lookup reads every page of its course code; any other search shows one page at a time.
                    query = new(year, term.Selector, state.CourseCode, state.CourseName, state.Instructor, state.SelectedCollege?.Code,
                        state.SelectedDepartment?.Code, lookup.Length > 0 ? null : 1, state.Grade, state.Level);
                }
                Model.Replace(state with { SearchState = CourseUiLoadState.Loading, Rows = [], SearchRead = null, IssueKey = null });
                if (generation != version) { query = null; return; }
                Details.Clear();
                if (generation != version) { query = null; return; }
                WatchSelection.ReplaceSelection([]);
                if (generation != version) query = null;
            }
            catch (ArgumentException) { Model.Replace(state with { SearchState = CourseUiLoadState.Invalid, IssueKey = "Ui.FiltersInvalid" }); }
        }, cancellationToken).ConfigureAwait(false);
        if (query is null || Volatile.Read(ref generation) != version) return;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await queries.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read.Data is { } data && data.Query != query) throw new InvalidOperationException("Unexpected search scope.");
            var verified = read.Data?.Rows.Where(row => row.Key.Year == query.Year
                && string.Concat(row.Key.SemesterCode, row.Key.DetailSemesterCode) == query.TermSelector).ToImmutableArray() ?? [];
            var dropped = verified.Length != (read.Data?.Rows.Length ?? 0);
            var rows = verified.Where(row => lookup.Length == 0 || row.Key.CourseCode == query.CourseCode && row.Key.Section == lookup)
                .Select(row => new CourseSearchItem(row, false)).ToImmutableArray();
            var status = read.Status == SourceReadStatus.Failed ? CourseUiLoadState.Failed
                : read.Status == SourceReadStatus.Partial || dropped ? CourseUiLoadState.Partial
                : rows.IsEmpty ? CourseUiLoadState.Empty : CourseUiLoadState.Complete;
            await PublishAsync(version, state => state with { SearchRead = read, Rows = rows, SearchState = status,
                IssueKey = status == CourseUiLoadState.Empty && lookup.Length > 0 ? "Ui.SectionNotFound" : null }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await PublishAsync(version, state => state with { SearchState = CourseUiLoadState.Canceled }).ConfigureAwait(false); }
        catch (Exception)
        {
            await PublishAsync(version, state => state with { SearchState = CourseUiLoadState.Failed, Rows = [], IssueKey = "Ui.DataUnavailable" }).ConfigureAwait(false);
            Report("ui_search_failed");
        }
    }

    private Task PublishAsync(long version, Func<CourseSearchSnapshot, CourseSearchSnapshot> update) => dispatcher.InvokeAsync(() =>
    {
        if (!disposed && generation == version) Model.Replace(update(Model.State));
    });
}
