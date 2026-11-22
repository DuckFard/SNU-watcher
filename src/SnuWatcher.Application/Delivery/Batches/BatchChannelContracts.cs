using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Delivery.Batches;

/// <summary>Zero initialization cannot authorize submission. Readiness never proves visibility.</summary>
public enum BatchChannelReadiness { Initializing = 0, Ready = 1, Disabled = 2, Unavailable = 3 }

/// <summary>Transient native evidence, explicitly mapped to the existing durable attempt outcomes.</summary>
public enum BatchChannelOutcome { NotSubmitted = 0, OsAccepted = 1, RetryableFailure = 2, AcceptanceUnknown = 3 }

/// <summary>Evidence that the adapter did not invoke the OS submission boundary.</summary>
public enum BatchNoSubmissionReason { Cancelled = 0, ChannelNotReady = 1, ValidityChanged = 2, PayloadRejected = 3 }

/// <summary>Known native acceptance survives late cancellation; no result implies that a banner was seen.</summary>
public sealed record BatchChannelResult
{
    public BatchChannelResult(BatchChannelOutcome outcome, BatchNoSubmissionReason? noSubmissionReason = null)
    {
        Outcome = PersistenceValidation.Defined(outcome, nameof(outcome));
        if (noSubmissionReason is { } reason) PersistenceValidation.Defined(reason, nameof(noSubmissionReason));
        if ((outcome == BatchChannelOutcome.NotSubmitted) != noSubmissionReason.HasValue)
            throw new ArgumentException("Only proven no-submission requires a no-submission reason.", nameof(noSubmissionReason));
        NoSubmissionReason = noSubmissionReason;
    }

    public BatchChannelOutcome Outcome { get; }
    public BatchNoSubmissionReason? NoSubmissionReason { get; }
}
