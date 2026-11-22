using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

public enum BatchDeliveryRunDisposition
{
    NoWork, MoreToFold, NeedsFreshCheck, NeedsRevalidation, NeedsRecovery, Closed,
    RecipientLimitReached, PendingResultLimit, ResultRecorded, ResultNotRecorded, ChannelDeferred
}

/// <summary>
/// Progress, actual channel outcome and durable receipt remain separate. A known command is immutable
/// retry authority, not a successful write. SubmittedThisCall means OS submission began or may have begun;
/// it is false for a flush/replay and for proven NotSubmitted, even after entering the channel adapter.
/// NeedsRecovery means blocked unresolved work; it never proves that another live channel has stopped.
/// </summary>
public sealed record BatchDeliveryRunResult
{
    internal BatchDeliveryRunResult(BatchDeliveryRunDisposition disposition, string reasonCode, Guid? batchId = null,
        Guid? attemptId = null, BatchChannelOutcome? channelOutcome = null, BatchAttemptResultCommand? knownResult = null,
        BatchAttemptReceipt? receipt = null, bool submittedThisCall = false, bool observerFailed = false)
    {
        Disposition = disposition;
        ReasonCode = reasonCode;
        BatchId = batchId;
        AttemptId = attemptId;
        ChannelOutcome = channelOutcome;
        KnownResult = knownResult;
        Receipt = receipt;
        SubmittedThisCall = submittedThisCall;
        ObserverFailed = observerFailed;
    }
    public BatchDeliveryRunDisposition Disposition { get; }
    public string ReasonCode { get; }
    public Guid? BatchId { get; }
    public Guid? AttemptId { get; }
    public BatchChannelOutcome? ChannelOutcome { get; }
    public BatchAttemptResultCommand? KnownResult { get; }
    public BatchAttemptReceipt? Receipt { get; }
    public bool SubmittedThisCall { get; }
    public bool ObserverFailed { get; }
    internal BatchDeliveryRunResult WithObserverFailure() => new(Disposition, ReasonCode, BatchId, AttemptId,
        ChannelOutcome, KnownResult, Receipt, SubmittedThisCall, true);
}

public sealed record BatchDeliveryAudit
{
    internal BatchDeliveryAudit(SectionKey key, BatchDeliveryRunResult result, DateTimeOffset completedAtUtc)
    {
        Key = key;
        Result = result;
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
    }
    public SectionKey Key { get; }
    public BatchDeliveryRunResult Result { get; }
    public DateTimeOffset CompletedAtUtc { get; }
}
