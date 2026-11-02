using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Health;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;

namespace SnuWatcher.Windows.Presentation.Watches;

/// <summary>One replacement UI snapshot. A collapsed group has no measured member health.</summary>
public sealed record WatchListState
{
    internal WatchListState() { }
    /// <summary>Only the current bounded page.</summary>
    public ImmutableArray<WatchRow> Rows { get; internal init; } = [];
    /// <summary>Whether the first page was read successfully.</summary>
    public bool IsLoaded { get; internal init; }
    /// <summary>Whether a bounded page read owns the paging controls.</summary>
    public bool IsLoading { get; internal init; }
    /// <summary>One-based position, without claiming a total page count.</summary>
    public long PageNumber { get; internal init; } = 1;
    /// <summary>Whether a retained backward cursor exists.</summary>
    public bool HasPrevious { get; internal init; }
    /// <summary>Whether the source page has another forward cursor.</summary>
    public bool HasNext { get; internal init; }
    /// <summary>Older backward positions were discarded; First still reaches the beginning.</summary>
    public bool BackHistoryLimited { get; internal init; }
    /// <summary>Current action message resource key, never a raw exception.</summary>
    public string? MessageKey { get; internal init; }
    /// <summary>Paging notice independent of a mutation receipt.</summary>
    public string? PagingNoticeKey { get; internal init; }
    /// <summary>Stable selection, including a watch outside the visible page.</summary>
    public Guid? SelectedId { get; internal init; }
    /// <summary>All current members from one atomic details read.</summary>
    public WatchDetailsView? Details { get; internal init; }
    /// <summary>The exact member to refresh manually.</summary>
    public SectionKey? SelectedMemberKey { get; internal init; }
    /// <summary>Whether the latest selected details are pending.</summary>
    public bool SelectionPending { get; internal init; }
    /// <summary>Whether a definition mutation owns the controls.</summary>
    public bool IsMutating { get; internal init; }
    /// <summary>Editable label, separate from the confirmed definition.</summary>
    public string RenameText { get; internal init; } = "";
    /// <summary>Whether the next expiry edit removes the boundary.</summary>
    public bool NoExpiry { get; internal init; } = true;
    /// <summary>Korea calendar date chosen in the editor.</summary>
    public DateTime? ExpiryDate { get; internal init; }
}

/// <summary>One visible definition and its optional single-section state.</summary>
public sealed record WatchRow
{
    internal WatchRow(WatchDefinitionSummary definition, SectionHealthSnapshot? health, WatchStatus status, string? termLabel)
    { Definition = definition; Health = health; Status = status; TermLabel = termLabel; }
    /// <summary>The exact immutable definition summary.</summary>
    public WatchDefinitionSummary Definition { get; }
    /// <summary>Null for collapsed groups, whose coverage is unmeasured.</summary>
    public SectionHealthSnapshot? Health { get; }
    /// <summary>Effective lifecycle, independent of source health.</summary>
    public WatchStatus Status { get; }
    /// <summary>Source-derived display hint only, never term authority.</summary>
    public string? TermLabel { get; }
    /// <summary>Stable watch identity.</summary>
    public Guid Id => Definition.Id;
    /// <summary>Original saved label.</summary>
    public string Label => Definition.Label;
    /// <summary>Full academic identity used for grouping.</summary>
    public AcademicTermIdentity Term => Definition.Term;
    /// <summary>Exact member count, including unloaded groups.</summary>
    public int MemberCount => Definition.MemberCount;
    /// <summary>Exact code and leading-zero section, when a single-section watch.</summary>
    public string Identifier => Health is null ? "—" : WatchFormatting.Identifier(Health.Key);
    /// <summary>Last known count pair, even if unavailable now.</summary>
    public string CountsText => WatchFormatting.Counts(Health);
    /// <summary>Lifecycle resource key.</summary>
    public string StatusKey => WatchFormatting.Status(Status);
    /// <summary>No full/open claim is made for an unmeasured group.</summary>
    public string AvailabilityKey => Health is null ? "Ui.GroupUnmeasured" : WatchFormatting.Availability(Health.Availability);
    /// <summary>Current data-health key.</summary>
    public string DataStateKey => Health is null ? "Ui.GroupUnmeasured" : WatchFormatting.DataState(Health.DataState);
    /// <summary>Original last successful observation, formatted in Korea time.</summary>
    public string LastSuccessText => WatchFormatting.Time(Health?.LastSuccessfulAtUtc);
}

/// <summary>One selected membership with pure health derived from one evaluation instant.</summary>
public sealed record WatchDetailsView
{
    internal WatchDetailsView(WatchDetailsSnapshot source, GroupHealthSnapshot health, IEnumerable<WatchMemberRow> members)
    { Source = source; Health = health; Members = members.ToImmutableArray(); }
    /// <summary>Exact atomic source for local age-only reprojection.</summary>
    public WatchDetailsSnapshot Source { get; }
    /// <summary>Coverage and lifecycle for the entire current membership.</summary>
    public GroupHealthSnapshot Health { get; }
    /// <summary>Every exact member, with no truncation.</summary>
    public ImmutableArray<WatchMemberRow> Members { get; }
    /// <summary>Original saved label.</summary>
    public string Label => Source.Subscription.Label;
    /// <summary>Current verified coverage, separate from apparent availability.</summary>
    public string CoverageText => $"{Health.CoverageCount}/{Health.MemberCount}";
    /// <summary>Lifecycle key.</summary>
    public string StatusKey => WatchFormatting.Status(Health.EffectiveWatchStatus);
    /// <summary>All-full is possible only with complete current coverage.</summary>
    public string AvailabilityKey => WatchFormatting.Availability(Health.Availability);
}

/// <summary>One member's unchanged counts and original timestamps alongside current health.</summary>
public sealed record WatchMemberRow
{
    internal WatchMemberRow(SectionHealthSnapshot health) => Health = health;
    /// <summary>Pure health projection retaining historical facts.</summary>
    public SectionHealthSnapshot Health { get; }
    /// <summary>Full identity for selection and manual refresh.</summary>
    public SectionKey Key => Health.Key;
    /// <summary>Exact code and leading-zero section.</summary>
    public string Identifier => WatchFormatting.Identifier(Key);
    /// <summary>Last known count pair; not a claim of current availability.</summary>
    public string CountsText => WatchFormatting.Counts(Health);
    /// <summary>Apparent seats only when current, otherwise unavailable.</summary>
    public string CurrentSeatsText => Health.Availability == AvailabilityState.Unknown ? "—"
        : Health.LastKnownCounts!.Value.ApparentSeats.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Current health resource key.</summary>
    public string DataStateKey => WatchFormatting.DataState(Health.DataState);
    /// <summary>Current availability resource key.</summary>
    public string AvailabilityKey => WatchFormatting.Availability(Health.Availability);
    /// <summary>Latest check result, independent of last success.</summary>
    public string OutcomeKey => WatchFormatting.Outcome(Health.LatestOutcome);
    /// <summary>Last successful source time in Korea time.</summary>
    public string LastSuccessText => WatchFormatting.Time(Health.LastSuccessfulAtUtc);
    /// <summary>Latest completed attempt time in Korea time.</summary>
    public string LastCheckText => WatchFormatting.Time(Health.LastCompletedAttemptAtUtc);
    /// <summary>Age of the original successful observation; no false zero for missing/future facts.</summary>
    public string AgeText => Health.Age?.ToString("c", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
}
