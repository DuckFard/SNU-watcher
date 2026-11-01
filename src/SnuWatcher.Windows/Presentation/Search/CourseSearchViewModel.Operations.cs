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

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        CourseSearchQuery? query = null;
        string lookup = "";
        long version = 0;
        await dispatcher.InvokeAsync(() =>
        {
            version = generation;
            var state = Model.State;
            try
            {
                if (state.SelectedYear is not { } year || state.SelectedTerm is not { } term
                    || !state.Terms.Any(option => option.Year == year && option.Selector == term.Selector))
                { Model.Replace(state with { SearchState = CourseUiLoadState.Invalid, IssueKey = "Ui.TermUnconfirmed" }); return; }
                lookup = state.SectionLookup.Trim();
                if (lookup.Length > 0 && (lookup.Length != 3 || lookup.Any(character => character is < '0' or > '9') || string.IsNullOrWhiteSpace(state.CourseCode)))
                { Model.Replace(state with { SearchState = CourseUiLoadState.Invalid, IssueKey = "Ui.InvalidSection" }); return; }
                query = new(year, term.Selector, state.CourseCode, state.CourseName, state.Instructor, state.SelectedCollege?.Code, state.SelectedDepartment?.Code);
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
