using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

/// <summary>
/// Bounded folding yields outside the shared lifecycle/send gate. Current validation, reservation,
/// the actual driver operation and result recording remain inside it; polling must never use this gate.
/// All store calls finish their SQL transaction before returning. The host retains its database owner
/// until actual driver work and known-result flushes stop, and recovers old unresolved attempts before
/// starting live workers. This coordinator never interprets an unresolved attempt as safe to recover.
/// </summary>
public sealed class BatchDeliveryCoordinator
{
    private readonly IBatchDeliveryStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate;
    private readonly IBatchDesktopAlertChannel _channel;
    private readonly Action<BatchDeliveryAudit>? _observer;
    private readonly int _capacity;
    // Every access is under _gate. Holding it from capacity check through retention reserves one slot
    // without another lock. The map contains no event arrays or recipient payloads.
    private readonly Dictionary<SectionKey, KnownResult> _known = [];

    public BatchDeliveryCoordinator(IBatchDeliveryStore store, TimeProvider clock, SemaphoreSlim lifecycleSendGate,
        IBatchDesktopAlertChannel channel, Action<BatchDeliveryAudit>? observer = null, int maxPendingKnownResults = 128)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lifecycleSendGate);
        ArgumentNullException.ThrowIfNull(channel);
        if (maxPendingKnownResults is < 1 or > BatchDeliveryLimits.MaxRecipients) throw new ArgumentOutOfRangeException(nameof(maxPendingKnownResults));
        _store = store;
        _clock = clock;
        _gate = lifecycleSendGate;
        _channel = channel;
        _observer = observer;
        _capacity = maxPendingKnownResults;
    }

    public async Task<BatchDeliveryRunResult> TryDeliverSectionAsync(SectionKey key, BatchDeliveryPolicy policy,
        int pageSize = 128, int maxFoldPages = 8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(policy);
        if (pageSize is < 1 or > BatchDeliveryLimits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maxFoldPages is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maxFoldPages));
        var result = await RunAsync(key, policy, pageSize, maxFoldPages, cancellationToken).ConfigureAwait(false);
        // An observer may reenter lifecycle work and must not run under the shared gate.
        try { _observer?.Invoke(new(key, result, _clock.GetUtcNow())); }
        catch (Exception) { return result.WithObserverFailure(); }
        return result;
    }

    /// <summary>
    /// Priority/shutdown work that may no longer appear in the database's pending page after a lost
    /// committed-write return. A single delivery pump may prioritize these keys before new work. For
    /// shutdown, stop and await every delivery caller before retrying the keys with an uncancelled token.
    /// This snapshot is not a general flush-only operation: a concurrent caller can remove a key before
    /// TryDeliverSectionAsync, allowing that later call to process new work.
    /// </summary>
    public async Task<ImmutableArray<SectionKey>> ReadPendingKnownSectionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _known.Keys.ToImmutableArray(); }
        finally { _gate.Release(); }
    }

    private async Task<BatchDeliveryRunResult> RunAsync(SectionKey key, BatchDeliveryPolicy policy, int pageSize,
        int maxFoldPages, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await PendingResultAsync(key).ConfigureAwait(false) is { } prior) return prior;
        }
        finally { _gate.Release(); }

        BatchProjection projection = null!;
        for (var page = 0; page < maxFoldPages; page++)
        {
            projection = await _store.ProjectDeliveryBatchAsync(new(key, pageSize, _clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            if (projection.Status != BatchProjectionStatus.MoreToFold) break;
            if (page + 1 < maxFoldPages) await Task.Yield();
        }
        if (projection.Status == BatchProjectionStatus.NoWork) return new(BatchDeliveryRunDisposition.NoWork, "no_work");
        if (projection.Status == BatchProjectionStatus.MoreToFold)
            return new(BatchDeliveryRunDisposition.MoreToFold, "fold_incomplete", projection.Batch!.Id);
        if (projection.Status == BatchProjectionStatus.UnresolvedAttempt)
            return new(BatchDeliveryRunDisposition.NeedsRecovery, "attempt_unresolved", projection.Batch!.Id, projection.UnresolvedAttemptId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await PendingResultAsync(key).ConfigureAwait(false) is { } prior) return prior;
            return await PrepareAndSendAsync(key, projection.Batch!.Id, policy, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<BatchDeliveryRunResult?> PendingResultAsync(SectionKey key)
    {
        if (_known.TryGetValue(key, out var known)) return await RecordAsync(key, known, submitted: false).ConfigureAwait(false);
        return _known.Count >= _capacity ? new(BatchDeliveryRunDisposition.PendingResultLimit, "pending_result_limit") : null;
    }

    private async Task<BatchDeliveryRunResult> PrepareAndSendAsync(SectionKey key, Guid batchId,
        BatchDeliveryPolicy policy, CancellationToken cancellationToken)
    {
        var context = await _store.ReadDeliveryBatchContextAsync(batchId, policy, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Batch.Id != batchId || context.Batch.Key != key || context.Policy != policy)
            return new(BatchDeliveryRunDisposition.NeedsRevalidation, "batch_context_changed", batchId);
        var now = _clock.GetUtcNow();
        var decision = BatchDeliveryPlanner.Plan(context, now);
        if (decision.Disposition is not (BatchPlanDisposition.Send or BatchPlanDisposition.ResolveWithoutSend))
            return PlanResult(context, decision);
        BatchReservationResult reservation;
        var prepared = new PreparedBatchAttempt(Guid.NewGuid(), context, decision, now);
        try
        {
            reservation = await _store.TryReserveBatchAttemptAsync(prepared, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A lost return may hide a committed reservation. The next projection discovers its
            // unresolved identity; never allocate a replacement or guess that invocation began.
            return new(BatchDeliveryRunDisposition.NeedsRecovery, "reservation_result_unknown", batchId);
        }
        if (reservation.Batch.Id != batchId || reservation.Batch.Key != key
            || (reservation.Attempt is { } returned && returned.ReservationRequestId != prepared.ReservationRequestId))
            return new(BatchDeliveryRunDisposition.NeedsRecovery, "reservation_authority_mismatch", batchId);
        if (reservation.Status != BatchReservationStatus.Reserved) return ReservationResult(reservation);
        var attempt = reservation.Attempt!;
        var preflight = cancellationToken.IsCancellationRequested ? "not_submitted_cancelled"
            : Preflight(context, decision, attempt, policy);
        if (preflight is null && cancellationToken.IsCancellationRequested) preflight = "not_submitted_cancelled";
        if (preflight is not null)
            return await RetainAndRecordAsync(key, attempt, BatchAttemptResultOutcome.NotSubmitted, null, preflight, submitted: false).ConfigureAwait(false);
        var actual = await InvokeAsync(attempt.Envelope, cancellationToken).ConfigureAwait(false);
        return await RetainAndRecordAsync(key, attempt, actual.Result, actual.Channel, actual.Code, submitted: true).ConfigureAwait(false);
    }

    private string? Preflight(BatchDeliveryContext context, BatchDeliveryDecision decision, BatchAttemptSnapshot attempt, BatchDeliveryPolicy policy)
    {
        var content = decision.Content;
        var envelope = attempt.Envelope;
        if (content is null || attempt.Policy != policy || envelope.Key != content.Key || envelope.CurrentCounts != content.CurrentCounts
            || envelope.CurrentSuccessAtUtc != content.CurrentSuccessAtUtc || envelope.CurrentSourceOrdinal != content.CurrentSourceOrdinal
            || envelope.Coverage != content.Coverage || !BatchValidation.SameRecipients(envelope.EligibleRecipients, content.EligibleRecipients))
            return "not_submitted_facts_changed";
        var current = BatchDeliveryPlanner.Plan(context, _clock.GetUtcNow());
        return current.Disposition == BatchPlanDisposition.Send ? null : "not_submitted_" + current.ReasonCode;
    }

    private async Task<(BatchAttemptResultOutcome Result, DesktopAlertOutcome? Channel, string Code)> InvokeAsync(
        BatchDeliveryEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            return await _channel.SendAsync(envelope, cancellationToken).ConfigureAwait(false) switch
            {
                DesktopAlertOutcome.OsAccepted => (BatchAttemptResultOutcome.OsAccepted, DesktopAlertOutcome.OsAccepted, "os_accepted"),
                DesktopAlertOutcome.RetryableFailure => (BatchAttemptResultOutcome.RetryableFailure, DesktopAlertOutcome.RetryableFailure, "desktop_retryable_failure"),
                _ => (BatchAttemptResultOutcome.InterruptedUnknown, null, "channel_result_unknown"),
            };
        }
        // The driver contract guarantees the actual operation stopped before returning/throwing.
        // No returned OS result means uncertainty, including caller cancellation after invocation.
        catch (Exception) { return (BatchAttemptResultOutcome.InterruptedUnknown, null, "channel_result_unknown"); }
    }

    private async Task<BatchDeliveryRunResult> RetainAndRecordAsync(SectionKey key, BatchAttemptSnapshot attempt,
        BatchAttemptResultOutcome outcome, DesktopAlertOutcome? channel, string code, bool submitted)
    {
        var command = new BatchAttemptResultCommand(attempt.AttemptId, outcome, code, _clock.GetUtcNow());
        var known = new KnownResult(attempt.Envelope.BatchId, attempt.Envelope.Coverage.EventCount, command, channel);
        _known.Add(key, known);
        return await RecordAsync(key, known, submitted).ConfigureAwait(false);
    }

    private async Task<BatchDeliveryRunResult> RecordAsync(SectionKey key, KnownResult known, bool submitted)
    {
        try
        {
            // One bounded write per call. A failed/lost result stays in the map and is retried first
            // on the next call, with the identical command object and original recorded timestamp.
            var receipt = await _store.RecordBatchAttemptResultAsync(known.Command, CancellationToken.None).ConfigureAwait(false);
            if (receipt.AttemptId != known.Command.AttemptId || receipt.BatchId != known.BatchId
                || receipt.Outcome != known.Command.Outcome || receipt.CoveredEventCount != known.CoveredCount
                || receipt.RecordedAtUtc != known.Command.RecordedAtUtc)
                return KnownResultState(known, null, submitted, "result_receipt_mismatch");
            _known.Remove(key);
            return KnownResultState(known, receipt, submitted, known.Command.DiagnosticCode);
        }
        catch (Exception) { return KnownResultState(known, null, submitted, "result_not_recorded"); }
    }

    private static BatchDeliveryRunResult KnownResultState(KnownResult known, BatchAttemptReceipt? receipt, bool submitted, string code) =>
        new(receipt is null ? BatchDeliveryRunDisposition.ResultNotRecorded : BatchDeliveryRunDisposition.ResultRecorded,
            code, known.BatchId, known.Command.AttemptId, known.Channel, known.Command, receipt, submitted);

    private static BatchDeliveryRunResult PlanResult(BatchDeliveryContext context, BatchDeliveryDecision decision) =>
        new(decision.Disposition switch
        {
            BatchPlanDisposition.NeedsFreshCheck => BatchDeliveryRunDisposition.NeedsFreshCheck,
            BatchPlanDisposition.MoreToFold => BatchDeliveryRunDisposition.MoreToFold,
            BatchPlanDisposition.UnresolvedAttempt => BatchDeliveryRunDisposition.NeedsRecovery,
            BatchPlanDisposition.Closed => BatchDeliveryRunDisposition.Closed,
            BatchPlanDisposition.RecipientLimitReached => BatchDeliveryRunDisposition.RecipientLimitReached,
            _ => BatchDeliveryRunDisposition.NeedsRevalidation,
        }, decision.ReasonCode, context.Batch.Id, context.UnresolvedAttemptId);

    private static BatchDeliveryRunResult ReservationResult(BatchReservationResult reservation) =>
        new(reservation.Status switch
        {
            BatchReservationStatus.Replayed => reservation.Resolution is null ? BatchDeliveryRunDisposition.NeedsRecovery : BatchDeliveryRunDisposition.ResultRecorded,
            BatchReservationStatus.UnresolvedAttempt => BatchDeliveryRunDisposition.NeedsRecovery,
            BatchReservationStatus.NeedsFreshCheck => BatchDeliveryRunDisposition.NeedsFreshCheck,
            BatchReservationStatus.MoreToFold => BatchDeliveryRunDisposition.MoreToFold,
            BatchReservationStatus.Closed => BatchDeliveryRunDisposition.Closed,
            BatchReservationStatus.RecipientLimitReached => BatchDeliveryRunDisposition.RecipientLimitReached,
            _ => BatchDeliveryRunDisposition.NeedsRevalidation,
        }, reservation.ReasonCode, reservation.Batch.Id, reservation.Attempt?.AttemptId ?? reservation.UnresolvedAttemptId,
            receipt: reservation.Resolution);

    private sealed record KnownResult(Guid BatchId, long CoveredCount, BatchAttemptResultCommand Command, DesktopAlertOutcome? Channel);
}
