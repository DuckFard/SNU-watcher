using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

public enum CommitDisposition { AcceptedValid, AcceptedUnknown, RevalidationRequired, IgnoredStale }
public enum CommitWriteStatus { Committed, Replayed, VersionConflict }

/// <summary>Durable original result; identical replays return the same receipt and event ID.</summary>
public sealed record SectionCommitReceipt
{
    public SectionCommitReceipt(CheckAttemptToken token, SectionKey key, CommitDisposition disposition,
        long committedSectionVersion, Guid? eventId)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(key);
        Disposition = PersistenceValidation.Defined(disposition, nameof(disposition));
        CommittedSectionVersion = PersistenceValidation.Nonnegative(committedSectionVersion, nameof(committedSectionVersion));
        if (eventId is { } id)
        {
            PersistenceValidation.Id(id, nameof(eventId));
            if (disposition != CommitDisposition.AcceptedValid) throw new ArgumentException("Only a valid accepted observation can create an event.", nameof(eventId));
        }
        Token = token;
        Key = key;
        EventId = eventId;
    }

    public CheckAttemptToken Token { get; }
    public SectionKey Key { get; }
    public CommitDisposition Disposition { get; }
    public long CommittedSectionVersion { get; }
    public Guid? EventId { get; }
}

public sealed record TryCommitResult
{
    private TryCommitResult(CommitWriteStatus status, SectionCommitReceipt? receipt) { Status = status; Receipt = receipt; }
    public CommitWriteStatus Status { get; }
    public SectionCommitReceipt? Receipt { get; }

    public static TryCommitResult Committed(SectionCommitReceipt receipt) => new(CommitWriteStatus.Committed, receipt ?? throw new ArgumentNullException(nameof(receipt)));
    public static TryCommitResult Replayed(SectionCommitReceipt receipt) => new(CommitWriteStatus.Replayed, receipt ?? throw new ArgumentNullException(nameof(receipt)));
    public static TryCommitResult VersionConflict() => new(CommitWriteStatus.VersionConflict, null);
}

public sealed class ObservationReplayConflictException() : InvalidOperationException("This issued section completion already has different recorded source facts.");
public sealed class StoreVersionConflictException() : InvalidOperationException("Stored state changed. Reload before retrying this operation.");
