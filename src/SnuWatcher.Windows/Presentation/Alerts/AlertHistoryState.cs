using System.Collections.Immutable;

namespace SnuWatcher.Windows.Presentation.Alerts;

/// <summary>Bounded immutable history rendering, with read failures distinct from empty successful pages.</summary>
public sealed record AlertHistoryState
{
    /// <summary>At most 25 historical event rows.</summary>
    public ImmutableArray<AlertHistoryRow> Rows { get; init; } = [];
    /// <summary>Whether a global page was successfully read.</summary>
    public bool IsLoaded { get; init; }
    /// <summary>Whether any owned global page command is active.</summary>
    public bool IsLoading { get; init; }
    /// <summary>Retained rows failed their latest attempted read.</summary>
    public bool IsStale { get; init; }
    /// <summary>Original successful page-read stamp; failure never advances it.</summary>
    public DateTimeOffset? LastSuccessfulReadAtUtc { get; init; }
    /// <summary>Current ordinal page position, not a total page count.</summary>
    public long PageNumber { get; init; } = 1;
    /// <summary>An exact older continuation exists.</summary>
    public bool HasOlder { get; init; }
    /// <summary>A newer position remains within the retained 128-entry window.</summary>
    public bool HasNewer { get; init; }
    /// <summary>Earlier backward positions have been discarded; Latest remains available.</summary>
    public bool BackHistoryLimited { get; init; }
    /// <summary>Fixed global read issue key.</summary>
    public string? MessageKey { get; init; }
    /// <summary>Exact selected event, including during loading or a missing-event response.</summary>
    public Guid? SelectedEventId { get; init; }
    /// <summary>Bounded latest selection is still being read.</summary>
    public bool SelectionPending { get; init; }
    /// <summary>Fresh scalar event/durable-state projection for the selected identity.</summary>
    public AlertHistoryRow? Details { get; init; }
    /// <summary>Last successful scalar detail read completion; distinct from original observation time.</summary>
    public DateTimeOffset? DetailsReadAtUtc { get; init; }
    /// <summary>Retained scalar details failed to reload.</summary>
    public bool DetailsStale { get; init; }
    /// <summary>Fixed selected-event issue key.</summary>
    public string? DetailsMessageKey { get; init; }
    /// <summary>At most 25 original recipient associations.</summary>
    public ImmutableArray<AlertRecipientRow> Recipients { get; init; } = [];
    /// <summary>Current recipient page number.</summary>
    public long RecipientPageNumber { get; init; } = 1;
    /// <summary>Recipient continuation has more records.</summary>
    public bool HasNextRecipients { get; init; }
    /// <summary>A retained recipient position exists.</summary>
    public bool HasPreviousRecipients { get; init; }
    /// <summary>Recipient page is retained after a failed read.</summary>
    public bool RecipientsStale { get; init; }
    /// <summary>Fixed recipient issue key.</summary>
    public string? RecipientMessageKey { get; init; }
    /// <summary>At most 25 associated batch/event-result records.</summary>
    public ImmutableArray<AlertRecordRow> Attempts { get; init; } = [];
    /// <summary>Current source-bucket record page number.</summary>
    public long AttemptPageNumber { get; init; } = 1;
    /// <summary>Record continuation has more buckets/records.</summary>
    public bool HasNextAttempts { get; init; }
    /// <summary>A retained record position exists.</summary>
    public bool HasPreviousAttempts { get; init; }
    /// <summary>Records are retained after a failed read.</summary>
    public bool AttemptsStale { get; init; }
    /// <summary>Fixed record issue key.</summary>
    public string? AttemptMessageKey { get; init; }
}
