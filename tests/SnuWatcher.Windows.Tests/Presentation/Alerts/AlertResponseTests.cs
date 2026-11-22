using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Alerts;

public sealed class AlertResponseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LatePageFromOldSelectedEventCannotReplaceNewSelectionOrItsErrors(bool recipients)
    {
        await using var f = new AlertFixture();
        var first = AlertFixture.Id(1); var second = AlertFixture.Id(2);
        f.Reader.Page = (_, _) => Task.FromResult(new AlertHistoryPage(AlertFixture.Stamp, 1000,
            [AlertFixture.Item(1), AlertFixture.Item(2, 999)], false, null));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRecipient = new TaskCompletionSource<AlertRecipientPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldAttempt = new TaskCompletionSource<AlertAttemptPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reader.Recipients = (id, after, _) =>
        {
            if (recipients && id == first && after is not null) { entered.TrySetResult(); return oldRecipient.Task; }
            var item = new AlertHistoryRecipient(AlertFixture.Id(10), 1, SnuWatcher.Core.OpeningReason.IncreasedSeats, "Old");
            return Task.FromResult<AlertRecipientPage?>(new(id, AlertFixture.Stamp, id == first ? [item] : [],
                id == first, id == first ? new(id, item.SubscriptionId, 1) : null));
        };
        f.Reader.Attempts = (id, after, _) =>
        {
            if (!recipients && id == first && after is not null) { entered.TrySetResult(); return oldAttempt.Task; }
            var item = new AlertHistoryAttempt(id, null, new(2, SnuWatcher.Application.Persistence.DeliveryStatus.Pending, 0, AlertFixture.Now), true);
            return Task.FromResult<AlertAttemptPage?>(new(id, AlertFixture.Stamp, id == first ? [item] : [],
                id == first, id == first ? new(id, AlertAttemptOrigin.EventResult, 2) : null));
        };
        try
        {
            await f.Model.LoadCommand.ExecuteAsync();
            await f.Model.SelectEventAsync(first);
            var oldRead = (recipients ? f.Model.RecipientNextCommand : f.Model.AttemptNextCommand).ExecuteAsync();
            await entered.Task.WaitAsync(Timeout);
            Assert.True(await f.Model.SelectEventAsync(second).WaitAsync(Timeout));
            oldRecipient.TrySetResult(null); oldAttempt.TrySetResult(null);
            Assert.Equal(UiCommandOutcome.Canceled, await oldRead.WaitAsync(Timeout));
            Assert.Equal(second, f.Model.State.Details!.EventId);
            Assert.Empty(f.Model.State.Recipients);
            Assert.Empty(f.Model.State.Attempts);
            Assert.False(f.Model.State.RecipientsStale);
            Assert.False(f.Model.State.AttemptsStale);
        }
        finally { oldRecipient.TrySetResult(null); oldAttempt.TrySetResult(null); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingChildPageIsExplicitlyUnavailableRatherThanAnEmptySuccessfulHistory(bool recipients)
    {
        await using var f = new AlertFixture();
        if (recipients) f.Reader.Recipients = (_, _, _) => Task.FromResult<AlertRecipientPage?>(null);
        else f.Reader.Attempts = (_, _, _) => Task.FromResult<AlertAttemptPage?>(null);
        await f.Model.LoadCommand.ExecuteAsync();
        Assert.True(await f.Model.SelectEventAsync(AlertFixture.Id(1)));
        Assert.Equal("Ui.AlertEventUnavailable", recipients ? f.Model.State.RecipientMessageKey : f.Model.State.AttemptMessageKey);
        Assert.True(recipients ? f.Model.State.RecipientsStale : f.Model.State.AttemptsStale);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongEventChildPageCannotBecomeSelectedEventEvidence(bool recipients)
    {
        await using var f = new AlertFixture();
        if (recipients) f.Reader.Recipients = (_, _, _) => Task.FromResult<AlertRecipientPage?>(new(AlertFixture.Id(2), AlertFixture.Stamp, [], false, null));
        else f.Reader.Attempts = (_, _, _) => Task.FromResult<AlertAttemptPage?>(new(AlertFixture.Id(2), AlertFixture.Stamp, [], false, null));
        await f.Model.LoadCommand.ExecuteAsync();
        Assert.False(await f.Model.SelectEventAsync(AlertFixture.Id(1)));
        Assert.Null(f.Model.State.Details);
        Assert.Equal("Ui.AlertHistoryUnavailable", f.Model.State.DetailsMessageKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullOrOversizedGlobalPageCannotFabricateOrSilentlyTruncateRows(bool missing)
    {
        await using var f = new AlertFixture();
        f.Reader.Page = (_, _) => Task.FromResult(missing ? null! : new AlertHistoryPage(AlertFixture.Stamp, 1000,
            Enumerable.Range(1, 26).Select(number => AlertFixture.Item(number, 1001 - number)), false, null));
        await f.Model.LoadCommand.ExecuteAsync();
        Assert.False(f.Model.State.IsLoaded);
        Assert.Empty(f.Model.State.Rows);
        Assert.Equal("Ui.AlertHistoryUnavailable", f.Model.State.MessageKey);
    }

    [Fact]
    public async Task RowSetterIgnoresOffPageIdentityAndReprojectsCurrentIdentityInsteadOfCallerFacts()
    {
        await using var f = new AlertFixture();
        await f.Model.LoadCommand.ExecuteAsync();
        await f.Ui.InvokeAsync(() => f.Model.SelectedRow = new(AlertFixture.Item(9, 991)));
        Assert.Null(f.Model.SelectedRow);
        Assert.True(await f.Model.SelectEventAsync(AlertFixture.Id(1)));
        await f.Ui.InvokeAsync(() => f.Model.SelectedRow = f.Model.SelectedRow);
        await f.Ui.InvokeAsync(() => f.Model.SelectedRow = null);
        Assert.Null(f.Model.State.SelectedEventId);
        Assert.Null(f.Model.State.Details);
    }
}
