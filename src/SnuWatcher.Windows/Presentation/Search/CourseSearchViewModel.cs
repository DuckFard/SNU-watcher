using System.ComponentModel;
using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Navigation;

namespace SnuWatcher.Windows.Presentation.Search;

/// <summary>Owned interactive search and explicit current-result selection.</summary>
public sealed partial class CourseSearchViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IRuntimeCourseQueries queries;
    private readonly IUiDispatcher dispatcher;
    private readonly Action<string> reportError;
    private long generation;
    private bool disposed;

    /// <summary>Creates an empty search controller; construction and filter editing never request source data.</summary>
    public CourseSearchViewModel(IRuntimeCourseQueries queries, IRuntimeWatchCommands watches, IUiDispatcher dispatcher,
        TimeProvider clock, Action<string>? reportError = null, IStudentActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(queries); ArgumentNullException.ThrowIfNull(watches);
        ArgumentNullException.ThrowIfNull(dispatcher); ArgumentNullException.ThrowIfNull(clock);
        this.queries = queries; this.dispatcher = dispatcher; this.reportError = reportError ?? (_ => { });
        Model = new(CourseSearchSnapshot.Initial(Math.Clamp(clock.GetUtcNow().Year, 2013, 2200)), dispatcher, this.reportError);
        Details = new(queries, dispatcher, actions, this.reportError);
        WatchSelection = new(watches, dispatcher, clock, this.reportError);
        Model.PropertyChanged += (_, _) => NotifyBindings();
        LoadCatalogCommand = new(LoadCatalogAsync, dispatcher, this.reportError);
        SearchCommand = new(SearchAsync, dispatcher, this.reportError);
        ClearCommand = new(_ => dispatcher.InvokeAsync(() => Change(state => CourseSearchSnapshot.Initial(state.SelectedYear)
            with { Terms = state.Terms, SelectedTerm = state.SelectedTerm })), dispatcher, this.reportError);
        CancelCommand = new(_ => dispatcher.InvokeAsync(() =>
        {
            generation++; CancelReads();
            Model.Replace(Model.State with { SearchState = CourseUiLoadState.Canceled, CatalogState = CourseUiLoadState.Canceled });
        }), dispatcher, this.reportError);
    }
    /// <summary>Immutable filters, source reads and selected result-row snapshots.</summary>
    public UiSnapshotModel<CourseSearchSnapshot> Model { get; }
    /// <summary>One exact latest-selection details controller.</summary>
    public CourseDetailsViewModel Details { get; }
    /// <summary>Explicit confirmation of currently selected verified result rows.</summary>
    public WatchSelectionViewModel WatchSelection { get; }
    /// <summary>Explicit metadata load for the current year/term/college branch.</summary>
    public UiAsyncCommand LoadCatalogCommand { get; }
    /// <summary>Explicit search only; typing never executes this command.</summary>
    public UiAsyncCommand SearchCommand { get; }
    /// <summary>Clears dependent filters/results/selection without requesting data.</summary>
    public UiAsyncCommand ClearCommand { get; }
    /// <summary>Cancels obsolete read waiters while retaining actual command ownership.</summary>
    public UiAsyncCommand CancelCommand { get; }
    /// <summary>Selected academic year; changing it clears all dependent source selections.</summary>
    public int? SelectedYear { get => Model.State.SelectedYear; set => Change(state => state.SelectedYear == value ? state : CourseSearchSnapshot.Initial(value)); }
    /// <summary>A term from the currently confirmed catalog, never an offline hint or inferred selector.</summary>
    public CatalogTerm? SelectedTerm
    {
        get => Model.State.SelectedTerm;
        set => Change(state =>
        {
            var confirmed = value is null ? null : state.Terms.FirstOrDefault(term => term.Year == state.SelectedYear
                && term.Year == value.Year && term.Selector == value.Selector);
            if (state.SelectedTerm?.Year == confirmed?.Year && state.SelectedTerm?.Selector == confirmed?.Selector) return state;
            return state with { SelectedTerm = confirmed, SelectedCollege = null, SelectedDepartment = null,
                Colleges = [], Departments = [], CatalogRead = null };
        });
    }
    /// <summary>A verified UP_DEPT choice. Changing it clears department selection/options.</summary>
    public DepartmentOption? SelectedCollege
    {
        get => Model.State.SelectedCollege;
        set => Change(state =>
        {
            var confirmed = value is null ? null : state.Colleges.FirstOrDefault(option => option.Code == value.Code && option.Level == value.Level);
            if (SameDepartment(state.SelectedCollege, confirmed)) return state;
            return state with { SelectedCollege = confirmed, SelectedDepartment = null, Departments = [], CatalogRead = null };
        });
    }
    /// <summary>A verified DEPT choice from the selected branch; major filtering is unsupported.</summary>
    public DepartmentOption? SelectedDepartment
    {
        get => Model.State.SelectedDepartment;
        set => Change(state =>
        {
            var confirmed = value is null ? null : state.Departments.FirstOrDefault(option => option.Code == value.Code && option.Level == value.Level);
            return SameDepartment(state.SelectedDepartment, confirmed) ? state : state with { SelectedDepartment = confirmed };
        });
    }
    /// <summary>Literal course-code filter, validated only on explicit submission.</summary>
    public string CourseCode { get => Model.State.CourseCode; set => Change(state => state.CourseCode == (value ?? "") ? state : state with { CourseCode = value ?? "" }); }
    /// <summary>Course-name/keyword filter supported by the source.</summary>
    public string CourseName { get => Model.State.CourseName; set => Change(state => state.CourseName == (value ?? "") ? state : state with { CourseName = value ?? "" }); }
    /// <summary>Verified instructor-name filter.</summary>
    public string Instructor { get => Model.State.Instructor; set => Change(state => state.Instructor == (value ?? "") ? state : state with { Instructor = value ?? "" }); }
    /// <summary>Optional three-ASCII-digit local lookup within an exact course-code result.</summary>
    public string SectionLookup { get => Model.State.SectionLookup; set => Change(state => state.SectionLookup == (value ?? "") ? state : state with { SectionLookup = value ?? "" }); }
    /// <summary>Binding changes are always raised on the UI dispatcher.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Change(Func<CourseSearchSnapshot, CourseSearchSnapshot> update)
    {
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Search editing requires the UI dispatcher.");
        ObjectDisposedException.ThrowIf(disposed, this);
        var before = Model.State;
        var updated = update(before);
        if (ReferenceEquals(updated, before)) return;
        generation++;
        // Publish the new intent before child cleanup can synchronously reenter through bindings.
        // Equivalent setter echoes return above; genuinely different nested edits remain effective.
        Model.Replace(updated with { Rows = [], SearchRead = null, SearchState = CourseUiLoadState.Idle, IssueKey = null });
        CancelReads(); Details.Clear(); WatchSelection.ReplaceSelection([]);
    }
    private static bool SameDepartment(DepartmentOption? first, DepartmentOption? second) => first?.Code == second?.Code && first?.Level == second?.Level;
    private void CancelReads() { LoadCatalogCommand.Cancel(); SearchCommand.Cancel(); Details.Cancel(); }

    /// <summary>Resolves supplied identity against current results; supplied row counts are never trusted.</summary>
    public async Task SelectRowAsync(CourseSearchRow? row, CancellationToken cancellationToken = default)
    {
        Task actual = Task.CompletedTask;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var key = row is null ? null : Model.State.Rows.FirstOrDefault(item => item.Row.Key == row.Key)?.Row.Key;
                if (key is null) Details.Clear();
                else actual = Details.LoadConfirmedAsync(key, Model.State.SelectedTerm?.Name, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            // Current-result resolution and admission share one dispatcher action.
            await actual.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception) { Report("ui_details_publication_failed"); }
    }

    /// <summary>Replaces the explicit selection using only current verified rows, bounded to 2000 sections.</summary>
    public void ToggleSection(SectionKey key, bool selected)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Search selection requires the UI dispatcher.");
        if (!WatchSelection.Model.State.CanEdit || !Model.State.Rows.Any(item => item.Row.Key == key)) return;
        var rows = Model.State.Rows.Select(item => item.Row.Key == key ? item with { IsSelected = selected } : item).ToImmutableArray();
        var confirmed = rows.Where(item => item.IsSelected).Select(item => item.Row).ToImmutableArray();
        if (confirmed.Length > SnuWatcher.Application.Persistence.PersistenceLimits.MaxSections)
        { Model.Replace(Model.State with { IssueKey = "Ui.SelectionLimit" }); return; }
        WatchSelection.ReplaceSelection(confirmed, Model.State.SelectedTerm?.Name);
        Model.Replace(Model.State with { Rows = rows, IssueKey = null });
    }

    private void NotifyBindings()
    {
        foreach (var observer in PropertyChanged?.GetInvocationList() ?? [])
        {
            try { ((PropertyChangedEventHandler)observer)(this, new(string.Empty)); }
            catch (Exception) { Report("ui_state_notification_failed"); }
        }
    }
    private void Report(string code) { try { reportError(code); } catch (Exception) { } }
    /// <summary>Closes read/action/mutation admission and drains actual work, including any admitted watch creation.</summary>
    public async ValueTask DisposeAsync()
    {
        disposed = true; Interlocked.Increment(ref generation);
        await LoadCatalogCommand.DisposeAsync().ConfigureAwait(false);
        await SearchCommand.DisposeAsync().ConfigureAwait(false);
        await ClearCommand.DisposeAsync().ConfigureAwait(false);
        await CancelCommand.DisposeAsync().ConfigureAwait(false);
        await Details.DisposeAsync().ConfigureAwait(false);
        await WatchSelection.DisposeAsync().ConfigureAwait(false);
    }
}
