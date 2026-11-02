using System.Collections.Immutable;

namespace SnuWatcher.Application.Delivery.Batches;

public sealed record DeliveryRecoverySummary
{
    internal DeliveryRecoverySummary(long recoveredAttempts, long coveredEvents)
    {
        RecoveredAttempts = recoveredAttempts;
        CoveredEvents = coveredEvents;
    }
    public long RecoveredAttempts { get; }
    public long CoveredEvents { get; }
}

/// <summary>
/// One-shot startup barrier. The caller MUST hold the production database-owner lease and establish
/// that no previous or current sender/reserver can still act. The shared gate is additional ordering,
/// not proof of that cross-process precondition. Do not invoke this alongside a delivery coordinator.
/// Complete recovery before starting live workers. Failure/cancellation aborts startup; retry requires
/// a fresh service under the same preconditions. No OS submission or source request occurs here.
/// </summary>
public sealed class DeliveryStartupRecovery
{
    private readonly IDeliveryRecoveryStore _discovery;
    private readonly IBatchDeliveryStore _batches;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate;
    private int _started;

    public DeliveryStartupRecovery(IDeliveryRecoveryStore discovery, IBatchDeliveryStore batches,
        TimeProvider clock, SemaphoreSlim lifecycleSendGate)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lifecycleSendGate);
        _discovery = discovery;
        _batches = batches;
        _clock = clock;
        _gate = lifecycleSendGate;
    }

    public async Task<DeliveryRecoverySummary> RecoverAsync(int pageSize = 128, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > DeliveryRecoveryLimits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("Delivery startup recovery can run only once per service instance.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RecoverPagesAsync(pageSize, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<DeliveryRecoverySummary> RecoverPagesAsync(int pageSize, CancellationToken cancellationToken)
    {
        Guid? cursor = null;
        long attempts = 0, events = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReadPageAsync(cursor, pageSize, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePage(page, cursor, pageSize);
            foreach (var unresolved in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var command = new BatchAttemptResultCommand(unresolved.AttemptId,
                    BatchAttemptResultOutcome.InterruptedUnknown, "process_interrupted", _clock.GetUtcNow());
                var receipt = await RecordAsync(unresolved, command).ConfigureAwait(false);
                try { attempts = checked(attempts + 1); events = checked(events + receipt.CoveredEventCount); }
                catch (OverflowException) { throw Failure("summary_invalid"); }
            }
            // With no live reservers, a short page proves no further unresolved rows exist. Do not
            // turn cancellation after the final returned commit into an apparent lost write result.
            if (page.Length < pageSize) return new(attempts, events);
            cursor = page[^1].AttemptId;
            await Task.Yield();
        }
    }

    private async Task<ImmutableArray<UnresolvedDeliveryAttempt>> ReadPageAsync(Guid? cursor, int pageSize,
        CancellationToken cancellationToken)
    {
        try { return await _discovery.ReadUnresolvedAttemptsAsync(cursor, pageSize, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw Failure("discovery_failed"); }
    }

    private static void ValidatePage(ImmutableArray<UnresolvedDeliveryAttempt> page, Guid? cursor, int pageSize)
    {
        if (page.IsDefault || page.Length > pageSize || page.Any(row => row is null)) throw Failure("discovery_invalid");
        var previous = cursor?.ToString("N") ?? "";
        foreach (var row in page)
        {
            var next = row.AttemptId.ToString("N");
            if (StringComparer.Ordinal.Compare(next, previous) <= 0) throw Failure("discovery_invalid");
            previous = next;
        }
    }

    private async Task<BatchAttemptReceipt> RecordAsync(UnresolvedDeliveryAttempt unresolved, BatchAttemptResultCommand command)
    {
        for (var retry = 0; retry < 2; retry++)
        {
            BatchAttemptReceipt receipt;
            try
            {
                // Once this canonical command starts, finish its bounded same-object retry even if
                // shutdown cancellation arrives. Cancellation is checked between attempt rows/pages.
                receipt = await _batches.RecordBatchAttemptResultAsync(command, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (retry == 1) throw Failure("record_failed");
                continue;
            }
            if (receipt is null || receipt.AttemptId != unresolved.AttemptId || receipt.BatchId != unresolved.BatchId
                || receipt.Outcome != BatchAttemptResultOutcome.InterruptedUnknown || receipt.RecordedAtUtc != command.RecordedAtUtc)
                throw Failure("receipt_invalid");
            return receipt;
        }
        throw Failure("record_failed");
    }

    private static InvalidOperationException Failure(string code) =>
        new($"Delivery startup recovery stopped ({code}). Monitoring must remain stopped.");
}
