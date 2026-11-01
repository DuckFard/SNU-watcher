using System.Collections.Immutable;
using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

internal sealed class CoordinatorFixture : IDisposable
{
    internal CoordinatorClock Clock { get; } = new();
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal BatchPort Port { get; } = new();
    internal BatchChannel Channel { get; } = new();
    internal BatchDeliveryCoordinator Coordinator(Action<BatchDeliveryAudit>? observer = null, int capacity = 128) =>
        new(Port, Clock, Gate, Channel, observer, capacity);
    internal Task<BatchDeliveryRunResult> RunAsync(BatchDeliveryCoordinator coordinator, CancellationToken ct = default,
        int pages = 8, SectionKey? key = null) => coordinator.TryDeliverSectionAsync(key ?? BatchFixture.Key(), BatchFixture.Policy(),
            pageSize: 1, maxFoldPages: pages, cancellationToken: ct);
    public void Dispose() => Gate.Dispose();
}

internal sealed class CoordinatorClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = BatchFixture.Now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Scripted port responses, not an in-memory persistence implementation.</summary>
internal sealed class BatchPort : IBatchDeliveryStore
{
    internal List<ProjectDeliveryBatchCommand> Projects { get; } = [];
    internal List<PreparedBatchAttempt> Reservations { get; } = [];
    internal List<BatchAttemptResultCommand> Results { get; } = [];
    internal int Reads { get; private set; }
    internal Func<ProjectDeliveryBatchCommand, CancellationToken, Task<BatchProjection>> Project { get; set; } = (_, _) =>
        Task.FromResult(new BatchProjection(BatchProjectionStatus.ReadyToEvaluate, BatchFixture.Batch(), null));
    internal Func<DateTimeOffset, CancellationToken, Task<BatchDeliveryContext>> Read { get; set; } = (at, _) =>
        Task.FromResult(BatchFixture.Context(evaluatedAt: at));
    internal Func<PreparedBatchAttempt, CancellationToken, Task<BatchReservationResult>> Reserve { get; set; } = (command, _) =>
    {
        if (command.Decision.Content is not { } content)
            return Task.FromResult(new BatchReservationResult(BatchReservationStatus.Closed,
                BatchFixture.Batch(DeliveryBatchStatus.Closed), null, null, "closed"));
        var envelope = new BatchDeliveryEnvelope(command.Context.Batch.Id, BatchFixture.AttemptId, content.Key,
            content.CurrentCounts, content.CurrentSuccessAtUtc, content.CurrentSourceOrdinal, content.EligibleRecipients, content.Coverage);
        var attempt = new BatchAttemptSnapshot(BatchFixture.AttemptId, command.ReservationRequestId, 1,
            command.EvaluatedAtUtc, command.Context.Policy, envelope);
        return Task.FromResult(new BatchReservationResult(BatchReservationStatus.Reserved, command.Context.Batch, attempt, null, "reserved"));
    };
    internal Func<BatchAttemptResultCommand, CancellationToken, Task<BatchAttemptReceipt>> Record { get; set; } = (command, _) =>
        Task.FromResult(Receipt(command));
    internal static BatchAttemptReceipt Receipt(BatchAttemptResultCommand command) => new(command.AttemptId, BatchFixture.BatchId,
        command.Outcome, 2, command.Outcome == BatchAttemptResultOutcome.OsAccepted ? 2 : 0,
        command.Outcome == BatchAttemptResultOutcome.OsAccepted ? 0 : 2, 0, command.RecordedAtUtc);

    public Task<ImmutableArray<SectionDeliveryWork>> ReadPendingDeliverySectionsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult(ImmutableArray<SectionDeliveryWork>.Empty);
    public Task<BatchProjection> ProjectDeliveryBatchAsync(ProjectDeliveryBatchCommand command, CancellationToken cancellationToken)
    {
        Projects.Add(command);
        return Project(command, cancellationToken);
    }
    public Task<BatchDeliveryContext> ReadDeliveryBatchContextAsync(Guid batchId, BatchDeliveryPolicy policy,
        DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken)
    {
        Reads++;
        return Read(evaluatedAtUtc, cancellationToken);
    }
    public Task<BatchReservationResult> TryReserveBatchAttemptAsync(PreparedBatchAttempt command, CancellationToken cancellationToken)
    {
        Reservations.Add(command);
        return Reserve(command, cancellationToken);
    }
    public Task<BatchAttemptReceipt> RecordBatchAttemptResultAsync(BatchAttemptResultCommand command, CancellationToken cancellationToken)
    {
        Results.Add(command);
        return Record(command, cancellationToken);
    }
    public Task<ImmutableArray<BatchAttemptCoverageItem>> ReadBatchAttemptCoverageAsync(Guid attemptId,
        long afterTriggerOrdinal, int limit, CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<BatchAttemptCoverageItem>.Empty);
}

internal sealed class BatchChannel : IBatchDesktopAlertChannel
{
    internal List<BatchDeliveryEnvelope> Envelopes { get; } = [];
    internal Func<BatchDeliveryEnvelope, CancellationToken, Task<DesktopAlertOutcome>> Send { get; set; } = (_, _) =>
        Task.FromResult(DesktopAlertOutcome.OsAccepted);
    public Task<DesktopAlertOutcome> SendAsync(BatchDeliveryEnvelope envelope, CancellationToken cancellationToken)
    {
        Envelopes.Add(envelope);
        return Send(envelope, cancellationToken);
    }
}
