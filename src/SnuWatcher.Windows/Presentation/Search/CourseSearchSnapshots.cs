using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Windows.Presentation.Search;

/// <summary>Source read state, separate from course availability and watch lifecycle.</summary>
public enum CourseUiLoadState
{
    /// <summary>No read has been submitted for this selection.</summary>
    Idle,
    /// <summary>An owned read is running.</summary>
    Loading,
    /// <summary>A complete nonempty response is displayed.</summary>
    Complete,
    /// <summary>Only verified partial rows are displayed; missing rows remain unknown.</summary>
    Partial,
    /// <summary>A complete response contains no matching rows.</summary>
    Empty,
    /// <summary>The source or local read failed.</summary>
    Failed,
    /// <summary>The current read was canceled.</summary>
    Canceled,
    /// <summary>Inputs require correction before any request.</summary>
    Invalid,
}
/// <summary>Explicit selection/create state, including ambiguous outcomes requiring the same command retry.</summary>
public enum WatchSelectionStatus
{
    /// <summary>No section is selected.</summary>
    Empty,
    /// <summary>Selected sections may be edited and confirmed.</summary>
    Ready,
    /// <summary>The original create command remains owned.</summary>
    Saving,
    /// <summary>The actual creation receipt was returned and published.</summary>
    Saved,
    /// <summary>The same command may be retried; original facts remain frozen.</summary>
    RetryRequired,
    /// <summary>Confirmation inputs require correction.</summary>
    Invalid,
}

/// <summary>Display-only arithmetic over one original source row; never proof of personal eligibility.</summary>
/// <param name="Row">Original verified source facts with their own observation time.</param>
/// <param name="IsSelected">Explicit current-result selection.</param>
public sealed record CourseSearchItem(CourseSearchRow Row, bool IsSelected)
{
    /// <summary>Registered and quota values from the same row; a dash means unavailable.</summary>
    public string EnrollmentText => $"{Row.Registered?.ToString() ?? "—"} / {Row.Capacity?.ToString() ?? "—"}";
    /// <summary>Apparent remaining quota, unknown when either source count is absent.</summary>
    public int? ApparentSeats => Row.Registered is { } registered && Row.Capacity is { } capacity
        && QuotaLabels.Map(Row.QuotaLabel) is not null ? new SeatCount(registered, capacity).ApparentSeats : null;
}

/// <summary>Every change replaces the full immutable filter/result snapshot on the UI dispatcher.</summary>
/// <param name="SelectedYear">Explicit academic year input.</param>
/// <param name="SelectedTerm">Source-confirmed opaque term option.</param>
/// <param name="SelectedCollege">Verified UP_DEPT option.</param>
/// <param name="SelectedDepartment">Verified DEPT option.</param>
/// <param name="CourseCode">Literal course-code filter.</param>
/// <param name="CourseName">Source-supported name/keyword filter.</param>
/// <param name="Instructor">Source-supported instructor filter.</param>
/// <param name="SectionLookup">Optional exact three-digit section lookup.</param>
/// <param name="Terms">Current source-confirmed term options.</param>
/// <param name="Colleges">Current branch's verified college options.</param>
/// <param name="Departments">Current branch's verified department options.</param>
/// <param name="Rows">Current verified result rows and explicit selections.</param>
/// <param name="CatalogState">Metadata read state.</param>
/// <param name="SearchState">Search read state, independent of availability.</param>
/// <param name="CatalogRead">Original metadata read.</param>
/// <param name="SearchRead">Original search read including partial/failure evidence.</param>
/// <param name="IssueKey">Fixed localizable input/action message key.</param>
public sealed record CourseSearchSnapshot(int? SelectedYear, CatalogTerm? SelectedTerm, DepartmentOption? SelectedCollege,
    DepartmentOption? SelectedDepartment, string CourseCode, string CourseName, string Instructor, string SectionLookup,
    ImmutableArray<CatalogTerm> Terms, ImmutableArray<DepartmentOption> Colleges, ImmutableArray<DepartmentOption> Departments,
    ImmutableArray<CourseSearchItem> Rows, CourseUiLoadState CatalogState, CourseUiLoadState SearchState,
    SourceRead<CourseCatalogData>? CatalogRead, SourceRead<CourseSearchData>? SearchRead, string? IssueKey)
{
    /// <summary>Empty offline rendering state; this factory performs no source request.</summary>
    public static CourseSearchSnapshot Initial(int? year) => new(year, null, null, null, "", "", "", "", [], [], [], [],
        CourseUiLoadState.Idle, CourseUiLoadState.Idle, null, null, null);
}

/// <summary>One exact selection and its original metadata result.</summary>
/// <param name="Key">Exact selected six-part identity.</param>
/// <param name="Read">Original metadata result; capacity never replaces search quota counts.</param>
/// <param name="Status">Current read state.</param>
/// <param name="TermName">Optional source-reported display label; never an identity or inferred alias.</param>
public sealed record CourseDetailsSnapshot(SectionKey? Key, SourceRead<CourseDetails>? Read, CourseUiLoadState Status, string? TermName = null);

/// <summary>Confirmed rows remain immutable; an ambiguous save freezes its original command and editing state.</summary>
/// <param name="Rows">Explicit source-confirmed current selection.</param>
/// <param name="Label">User confirmation label.</param>
/// <param name="InitialNoticeEnabled">Creation-time initial availability notice choice.</param>
/// <param name="UseExpiry">Whether to include a Korean final-day expiry.</param>
/// <param name="ExpiryDate">Selected date-only input.</param>
/// <param name="Status">Confirmation/submission state.</param>
/// <param name="Created">Actual returned creation receipt.</param>
/// <param name="IssueKey">Fixed localizable message key.</param>
/// <param name="CanEdit">False while a create may already have been admitted.</param>
/// <param name="TermName">Optional source-confirmed term display label; not catalog authority.</param>
public sealed record WatchSelectionSnapshot(ImmutableArray<CourseSearchRow> Rows, string Label, bool InitialNoticeEnabled,
    bool UseExpiry, DateTime? ExpiryDate, WatchSelectionStatus Status, SubscriptionSnapshot? Created, string? IssueKey, bool CanEdit, string? TermName = null)
{
    /// <summary>Selected academic year, absent rather than indexing an empty confirmation list.</summary>
    public int? Year => Rows.IsEmpty ? null : Rows[0].Key.Year;
}
