using System.Collections.Immutable;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Presentation.Alerts;

public sealed class AlertHistoryContractTests
{
    private static readonly Guid Event = Guid.ParseExact("cccccccccccccccccccccccccccccccc", "N");
    private static readonly Guid Other = Guid.ParseExact("dddddddddddddddddddddddddddddddd", "N");
    private static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "012", "0");
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(9));
    private static UiReadStamp Stamp(long issued = 10) => new(1, issued, Now);
    private static AlertTriggerFacts Trigger(Guid? id = null, long ordinal = 7) => new(id ?? Event, Key, ordinal, 1, 0, Now, Now, 1, false, true);
    private static AlertHistoryItem Item(Guid? id = null, long ordinal = 7) => new(Trigger(id, ordinal), new(1, DeliveryStatus.Pending, 0, null, null, null));
    private static AlertBatchAttempt Batch(long sequence = 2) => new(Other, Other, sequence, Now,
        new(Key, new(119, 120), Now, 7, 1), null, null);

    [Fact]
    public void DefaultImmutableInputIsRejectedAsAnInvalidPageRatherThanAnUnexpectedRuntimeFailure()
    {
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, default(ImmutableArray<AlertHistoryItem>), false, null));
    }

    [Fact]
    public void CursorTypesBindExactScopeAndRejectUnsupportedVersionsOrHighWaterViolations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertHistoryCursor(0, 1, Event));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertHistoryCursor(1, 0, Event));
        Assert.Throws<ArgumentException>(() => new AlertHistoryCursor(1, 2, Event));
        Assert.Throws<ArgumentException>(() => new AlertHistoryCursor(1, 1, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertHistoryCursor(1, 1, Event, 2));
        var cursor = new AlertHistoryCursor(long.MaxValue, long.MaxValue, Event);
        Assert.Equal("global-all", cursor.Scope); Assert.Equal(1, cursor.Version);
        Assert.Throws<ArgumentException>(() => new AlertRecipientCursor(Guid.Empty, Event, 1));
        Assert.Throws<ArgumentException>(() => new AlertRecipientCursor(Event, Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertRecipientCursor(Event, Other, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertRecipientCursor(Event, Other, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertAttemptCursor(Event, (AlertAttemptOrigin)99, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertAttemptCursor(Event, AlertAttemptOrigin.EventResult, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertAttemptCursor(Event, AlertAttemptOrigin.BatchAttempt, 1, 2));
        Assert.Equal("event-recipients", new AlertRecipientCursor(Event, Other, 1).Scope);
        Assert.Equal("event-records", new AlertAttemptCursor(Event, AlertAttemptOrigin.EventResult, 1).Scope);
    }

    [Fact]
    public void TriggerFactsRequireRealOpeningCountsRecipientsAndSupportedReasons()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertTriggerFacts(Event, Key, 1, 0, null, Now, Now, 1, true, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertTriggerFacts(Event, Key, 1, 1, -1, Now, Now, 1, true, false));
        Assert.Throws<ArgumentException>(() => new AlertTriggerFacts(Event, Key, 1, 1, null, Now, Now, 0, true, false));
        Assert.Throws<ArgumentException>(() => new AlertTriggerFacts(Event, Key, 1, 1, null, Now, Now, 2001, true, false));
        Assert.Throws<ArgumentException>(() => new AlertTriggerFacts(Event, Key, 1, 1, null, Now, Now, 1, false, false));
        Assert.Throws<ArgumentNullException>(() => new AlertTriggerFacts(Event, null!, 1, 1, null, Now, Now, 1, true, false));
        Assert.Equal(TimeSpan.Zero, Trigger().ObservedAtUtc.Offset);
        Assert.Equal("012", Trigger().Key.Section);
    }

    [Fact]
    public void DurableDeliveryEvidenceCannotClaimForeignReservationsOrFutureBudgetRevisions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertEventResult(1, (DeliveryStatus)99, 0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertEventResult(1, DeliveryStatus.Pending, -1, Now));
        Assert.Throws<ArgumentException>(() => new AlertDeliveryState(1, DeliveryStatus.Pending, 0, null, Other, null));
        Assert.Throws<ArgumentException>(() => new AlertDeliveryState(1, DeliveryStatus.Pending, 0, Guid.Empty, null, null));
        Assert.Throws<ArgumentException>(() => new AlertDeliveryState(1, DeliveryStatus.Pending, 0, Other, Guid.Empty, null));
        Assert.Throws<ArgumentException>(() => new AlertDeliveryState(1, DeliveryStatus.Pending, 0, null, null, new(2, DeliveryStatus.Pending, 0, Now)));
        Assert.Throws<ArgumentException>(() => new AlertDeliveryState(2, DeliveryStatus.Pending, 0, null, null, new(2, DeliveryStatus.Pending, 1, Now)));
        var durable = new AlertDeliveryState(2, DeliveryStatus.Pending, 1, Other, null, new(2, DeliveryStatus.Pending, 1, Now));
        Assert.Equal(DeliveryStatus.Pending, durable.LastRecordedResult!.Status);
        Assert.Equal(TimeSpan.Zero, durable.LastRecordedResult.RecordedAtUtc.Offset);
        Assert.Throws<ArgumentNullException>(() => new AlertHistoryItem(null!, durable));
        Assert.Throws<ArgumentNullException>(() => new AlertHistoryItem(Trigger(), null!));
    }

    [Fact]
    public void BatchAndEventRecordSourcesPreserveUnknownOutcomeAndWholeBatchContext()
    {
        var content = new AlertBatchContent(Key, new(119, 120), Now, 7, 2001);
        Assert.Equal(2001, content.CoverageCount); // Event coverage is distinct from the recipient limit.
        Assert.Throws<ArgumentException>(() => new AlertBatchContent(Key, new(120, 120), Now, 7, 1));
        Assert.Throws<ArgumentNullException>(() => new AlertBatchContent(null!, new(119, 120), Now, 7, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertBatchContent(Key, new(119, 120), Now, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertBatchContent(Key, new(119, 120), Now, 7, 0));
        Assert.Throws<ArgumentException>(() => new AlertBatchAttempt(Event, Other, 1, Now, content, BatchAttemptResultOutcome.OsAccepted, null));
        Assert.Throws<ArgumentException>(() => new AlertBatchAttempt(Event, Other, 1, Now, content, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertBatchAttempt(Event, Other, 1, Now, content, (BatchAttemptResultOutcome)99, Now));
        Assert.Throws<ArgumentNullException>(() => new AlertBatchAttempt(Event, Other, 1, Now, null!, null, null));
        Assert.Throws<ArgumentException>(() => new AlertHistoryAttempt(Event, null, null, true));
        Assert.Throws<ArgumentException>(() => new AlertHistoryAttempt(Event, Batch(), new(1, DeliveryStatus.Pending, 0, Now), true));
        Assert.Throws<ArgumentException>(() => new AlertHistoryAttempt(Event, null, new(1, DeliveryStatus.Pending, 0, Now), false));
        var associated = new AlertHistoryAttempt(Event, Batch(), null, false);
        Assert.False(associated.CoversEvent); Assert.Null(associated.BatchAttempt!.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line\nfeed")]
    public void CurrentLabelsAreBoundedLiteralDisplayContext(string label)
    {
        Assert.Throws<ArgumentException>(() => new AlertHistoryRecipient(Other, 1, OpeningReason.InitialAvailability, label));
        Assert.Throws<ArgumentException>(() => new AlertHistoryRecipient(Other, 1, OpeningReason.InitialAvailability, new string('x', 121)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertHistoryRecipient(Other, 1, (OpeningReason)99, null));
        Assert.Equal("<label>", new AlertHistoryRecipient(Other, 1, OpeningReason.InitialAvailability, "<label>").CurrentWatchLabel);
    }

    [Fact]
    public void GlobalPagesRequireBoundedDescendingUniquePositionsAndExactContinuation()
    {
        var a = Item(Event, 7); var b = Item(Other, 6);
        var valid = new AlertHistoryPage(Stamp(), 10, [a, b], true, new(10, 6, Other));
        Assert.Equal(2, valid.Items.Length); Assert.True(valid.HasMore);
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, [a, a], false, null));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, [b, a], false, null));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 11, [a], false, null));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 6, [a], false, null));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, [a], true, null));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, [a], true, new(9, 7, Event)));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, [a], true, new(10, 7, Other)));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, [a], true, new(10, 6, Event)));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, [], true, new(10, 7, Event)));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, Enumerable.Repeat(a, 51), false, null));
        Assert.Throws<ArgumentException>(() => new AlertHistoryPage(Stamp(), 10, [null!], false, null));
        Assert.Throws<ArgumentNullException>(() => new AlertHistoryPage(Stamp(), 10, null!, false, null));
    }
}
