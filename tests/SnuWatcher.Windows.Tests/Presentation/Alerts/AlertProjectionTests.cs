using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Alerts;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Alerts;

public sealed class AlertProjectionTests
{
    [Theory]
    [InlineData(DeliveryStatus.Pending, "Ui.AlertPending")]
    [InlineData(DeliveryStatus.OsAccepted, "Ui.AlertOsAccepted")]
    [InlineData(DeliveryStatus.Failed, "Ui.AlertFailed")]
    [InlineData(DeliveryStatus.Superseded, "Ui.AlertSuperseded")]
    public void CurrentDurableStateNeverReplacesOriginalCountsOrImpliesSeenOrRegistration(DeliveryStatus status, string key)
    {
        var source = AlertFixture.Item(status: status);
        var row = new AlertHistoryRow(source);
        Assert.Equal(key, row.StatusKey);
        Assert.Equal(4, row.Source.Trigger.CurrentSeats);
        Assert.Equal("4", row.OriginalSeatsText);
        Assert.Contains("UTC", row.ObservedAtText);
        var revision = new AlertHistoryAttempt(source.EventId, null, new(2, status, 0, AlertFixture.Now), true);
        var record = new AlertRecordRow(revision);
        Assert.Equal(key, record.OutcomeKey);
        Assert.Equal("Ui.AlertEventResult", record.OriginKey);
        Assert.Equal("Ui.AlertEventResultNotSubmission", record.ContextKey);
        Assert.Null(record.BatchCountsText);
    }

    [Theory]
    [InlineData(null, "Ui.AlertUnresolved")]
    [InlineData(BatchAttemptResultOutcome.InterruptedUnknown, "Ui.AlertAcceptanceUnknown")]
    [InlineData(BatchAttemptResultOutcome.OsAccepted, "Ui.AlertOsAccepted")]
    [InlineData(BatchAttemptResultOutcome.RetryableFailure, "Ui.AlertRetryable")]
    [InlineData(BatchAttemptResultOutcome.NotSubmitted, "Ui.AlertNotSubmitted")]
    public void WholeBatchOutcomeAndCoverageStayDistinctFromAnEventResult(BatchAttemptResultOutcome? outcome, string key)
    {
        var content = new AlertBatchContent(AlertFixture.Key, new(118, 120), AlertFixture.Now, 5, 100);
        var batch = new AlertBatchAttempt(AlertFixture.Id(5), AlertFixture.Id(6), 9, AlertFixture.Now, content,
            outcome, outcome is null ? null : AlertFixture.Now.AddSeconds(1));
        var record = new AlertRecordRow(new(AlertFixture.Id(1), batch, null, false));
        Assert.Equal(key, record.OutcomeKey);
        Assert.Equal("Ui.AlertBatchAttempt", record.OriginKey);
        Assert.Equal("Ui.AlertAssociatedOnly", record.ContextKey);
        Assert.Equal("118/120", record.BatchCountsText);
        Assert.Contains("UTC", record.RecordedAtText);
        Assert.Equal("Ui.AlertCoversEvent", new AlertRecordRow(new(AlertFixture.Id(1), batch, null, true)).ContextKey);
    }

    [Theory]
    [InlineData(true, false, "Ui.AlertInitialReason")]
    [InlineData(false, true, "Ui.AlertIncreaseReason")]
    [InlineData(true, true, "Ui.AlertBothReasons")]
    public void OriginalReasonsAndUnresolvedAuthorityAreExplicit(bool initial, bool increase, string key)
    {
        var original = AlertFixture.Item();
        var facts = new AlertTriggerFacts(original.EventId, AlertFixture.Key, 1, 4, 0, AlertFixture.Now, AlertFixture.Now, 1, initial, increase);
        var row = new AlertHistoryRow(new(facts, new(1, DeliveryStatus.Pending, 0, AlertFixture.Id(4), AlertFixture.Id(5), null)));
        Assert.Equal(key, row.ReasonKey);
        Assert.Equal("Ui.AlertUnresolved", row.StatusKey);
        var recipient = new AlertRecipientRow(new(AlertFixture.Id(2), 1,
            initial ? SnuWatcher.Core.OpeningReason.InitialAvailability : SnuWatcher.Core.OpeningReason.IncreasedSeats, null));
        Assert.Equal(AlertFixture.Id(2).ToString("N"), recipient.WatchLabel);
        Assert.Equal(initial ? "Ui.AlertInitialReason" : "Ui.AlertIncreaseReason", recipient.ReasonKey);
    }
}
