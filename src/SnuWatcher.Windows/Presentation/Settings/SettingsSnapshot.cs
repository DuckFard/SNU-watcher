using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Notifications;

namespace SnuWatcher.Windows.Presentation.Settings;

/// <summary>Editable settings, kept separate from saved year/term and onboarding metadata.</summary>
/// <param name="Language">The student's unsaved language choice.</param>
/// <param name="DesiredProfile">The student's unsaved refresh target.</param>
/// <param name="CloseBehavior">The student's unsaved window-close choice.</param>
/// <param name="NotificationSoundMuted">The student's unsaved notification-audio choice.</param>
public sealed record SettingsEditor(UiLanguage Language, WatchMonitorProfile DesiredProfile, UiCloseBehavior CloseBehavior,
    bool NotificationSoundMuted = false)
{
    /// <summary>Copies only editable choices from a known preference snapshot.</summary>
    public static SettingsEditor From(UiPreferencesValues values) => new(values.Language, values.DesiredProfile,
        values.CloseBehavior, values.NotificationSoundMuted);
}

/// <summary>Replaced on the UI dispatcher; saved intent, unsaved edits and actual actor state stay distinct.</summary>
/// <param name="Editor">The current unsaved choices.</param>
/// <param name="Stored">The latest known saved preferences, or null before a successful local read.</param>
/// <param name="Applied">The actual actor snapshot, never an inference from save success.</param>
/// <param name="IsBusy">Whether the serialized operation lane currently owns work.</param>
/// <param name="HasConflict">Whether an explicit write encountered another saved revision or identity conflict.</param>
/// <param name="SaveUncertain">Whether the original immutable write still needs its definitive receipt.</param>
/// <param name="IssueKey">A fixed localizable Ui.* status key.</param>
/// <param name="LastOperationAtUtc">Local operation time, not a source-observation freshness claim.</param>
/// <param name="NativeStatus">Actual last native probe evidence, independent of preference intent.</param>
/// <param name="NativeTestReceipt">The current test's actual result, never proof a banner was seen.</param>
/// <param name="NativeTestFeedback">Optional human confirmation scoped to the current accepted test.</param>
/// <param name="IsNativeBusy">Whether an actual native probe/test owns its independent lane.</param>
/// <param name="NativeIssueKey">A fixed localizable native operation status.</param>
public sealed record SettingsSnapshot(SettingsEditor Editor, UiPreferencesSnapshot? Stored, WatchMonitorProfileSnapshot? Applied,
    bool IsBusy, bool HasConflict, bool SaveUncertain, string? IssueKey, DateTimeOffset? LastOperationAtUtc,
    NativeNotificationStatus? NativeStatus = null, NativeNotificationTestReceipt? NativeTestReceipt = null,
    SettingsNativeTestFeedback? NativeTestFeedback = null, bool IsNativeBusy = false, string? NativeIssueKey = null)
{
    /// <summary>Whether a successful local preferences read or write is available.</summary>
    public bool IsLoaded => Stored is not null;
    /// <summary>Whether editable values differ from the latest known saved values.</summary>
    public bool IsDirty => Editor != SettingsEditor.From(Stored?.Values ?? UiPreferencesValues.Defaults);
    /// <summary>Whether the saved desired profile and actual actor revision/profile differ.</summary>
    public bool IsApplyPending => Stored is { } stored && (Applied is not { } applied
        || applied.PreferencesRevision != stored.Revision || applied.Profile != stored.Values.DesiredProfile);
    /// <summary>Editing pauses while a stable write needs explicit resolution.</summary>
    public bool CanEdit => IsLoaded && !SaveUncertain;
    /// <summary>Stable product defaults before any local read.</summary>
    public static SettingsSnapshot Initial { get; } = new(SettingsEditor.From(UiPreferencesValues.Defaults), null, null, false, false, false, null, null);
}

/// <summary>Human banner observation is independent of OS acceptance and makes no sound-heard claim.</summary>
public enum NativeTestBannerConfirmation
{
    /// <summary>No current human observation is recorded.</summary>
    Unknown,
    /// <summary>The student reports seeing this identified test banner.</summary>
    Seen,
    /// <summary>The student reports not seeing this identified test banner.</summary>
    NotSeen,
}

/// <summary>One human observation tied to an exact accepted native test identity.</summary>
public sealed record SettingsNativeTestFeedback
{
    /// <summary>Creates a validated observation; the view model additionally checks the current accepted receipt.</summary>
    public SettingsNativeTestFeedback(Guid testId, NativeTestBannerConfirmation confirmation)
    {
        if (testId == Guid.Empty) throw new ArgumentException("A test identity is required.", nameof(testId));
        if (!Enum.IsDefined(confirmation)) throw new ArgumentOutOfRangeException(nameof(confirmation));
        TestId = testId; Confirmation = confirmation;
    }
    /// <summary>The exact accepted test being described.</summary>
    public Guid TestId { get; }
    /// <summary>The student's report, not a native API guarantee.</summary>
    public NativeTestBannerConfirmation Confirmation { get; }
}

/// <summary>Actual outcome of the latest processed coalesced term hint.</summary>
public enum SettingsHintStatus
{
    /// <summary>The latest hint is saved and its saved profile revision is applied.</summary>
    Saved,
    /// <summary>A definite conflict prevented the requested hint from being saved.</summary>
    Conflict,
    /// <summary>A save or profile application needs explicit retry or prior-write resolution.</summary>
    Pending,
}

/// <summary>Coalesced callers receive the actual latest processed hint result, not a promise every older hint was saved.</summary>
public sealed record SettingsHintResult
{
    /// <summary>Creates a bounded typed result over the actual latest known stored preferences.</summary>
    public SettingsHintResult(SettingsHintStatus status, UiPreferencesSnapshot currentSnapshot)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        Status = status; CurrentSnapshot = currentSnapshot;
    }
    /// <summary>The processed latest-hint outcome.</summary>
    public SettingsHintStatus Status { get; }
    /// <summary>The current stored snapshot; a Pending result does not claim this contains the requested hint.</summary>
    public UiPreferencesSnapshot CurrentSnapshot { get; }
}
