using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Presentation.Preferences;

public sealed record UiPreferencesSnapshot
{
    public UiPreferencesSnapshot(long revision, UiPreferencesValues values, DateTimeOffset? savedAtUtc)
    {
        Revision = PersistenceValidation.Nonnegative(revision, nameof(revision));
        ArgumentNullException.ThrowIfNull(values);
        if (revision == 0 && (values != UiPreferencesValues.Defaults || savedAtUtc is not null)
            || revision > 0 && savedAtUtc is null)
            throw new ArgumentException("Unsaved preferences are defaults without a saved time; a saved revision needs its time.", nameof(savedAtUtc));
        Values = values; SavedAtUtc = savedAtUtc?.ToUniversalTime();
    }
    public long Revision { get; }
    public UiPreferencesValues Values { get; }
    public DateTimeOffset? SavedAtUtc { get; }
}

public sealed record SaveUiPreferencesCommand
{
    public SaveUiPreferencesCommand(Guid commandId, long expectedRevision, UiPreferencesValues values)
    {
        CommandId = PersistenceValidation.Id(commandId, nameof(commandId));
        ExpectedRevision = PersistenceValidation.Nonnegative(expectedRevision, nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(values); Values = values;
    }
    public Guid CommandId { get; }
    public long ExpectedRevision { get; }
    public UiPreferencesValues Values { get; }
}

public sealed record UiPreferencesSaveReceipt
{
    public UiPreferencesSaveReceipt(Guid commandId, long previousRevision, UiPreferencesSnapshot savedSnapshot)
    {
        CommandId = PersistenceValidation.Id(commandId, nameof(commandId));
        PreviousRevision = PersistenceValidation.Nonnegative(previousRevision, nameof(previousRevision));
        if (previousRevision == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(previousRevision));
        ArgumentNullException.ThrowIfNull(savedSnapshot);
        if (savedSnapshot.Revision != previousRevision + 1)
            throw new ArgumentException("An accepted save advances exactly one revision.", nameof(savedSnapshot));
        SavedSnapshot = savedSnapshot;
    }
    public Guid CommandId { get; }
    public long PreviousRevision { get; }
    public UiPreferencesSnapshot SavedSnapshot { get; }
}

public enum UiPreferencesSaveStatus { Applied, Replayed, VersionConflict }

public sealed record UiPreferencesSaveResult
{
    public UiPreferencesSaveResult(UiPreferencesSaveStatus status, UiPreferencesSaveReceipt? receipt, UiPreferencesSnapshot currentSnapshot)
    {
        Status = PersistenceValidation.Defined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        if ((status == UiPreferencesSaveStatus.VersionConflict) != (receipt is null))
            throw new ArgumentException("Accepted saves need their receipt; a version conflict has none.", nameof(receipt));
        if (receipt is not null && (currentSnapshot.Revision < receipt.SavedSnapshot.Revision
            || currentSnapshot.Revision == receipt.SavedSnapshot.Revision && currentSnapshot != receipt.SavedSnapshot
            || status == UiPreferencesSaveStatus.Applied && currentSnapshot != receipt.SavedSnapshot))
            throw new ArgumentException("The current snapshot cannot precede or contradict the accepted receipt.", nameof(currentSnapshot));
        Receipt = receipt; CurrentSnapshot = currentSnapshot;
    }
    public UiPreferencesSaveStatus Status { get; }
    public UiPreferencesSaveReceipt? Receipt { get; }
    public UiPreferencesSnapshot CurrentSnapshot { get; }
}

/// <summary>Expected user-command conflict, distinct from unexpected local storage or payload failure.</summary>
public sealed class UiPreferencesReplayConflictException : InvalidOperationException
{
    public UiPreferencesReplayConflictException() : base("This preference save identity was already accepted with different command facts.") { }
}
