using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Navigation;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Alerts;

public sealed class AlertPagingTests
{
    [Fact]
    public async Task OlderCanContinuePast128PositionsWhileNewerUsesOnlyRetainedCursorsAndLatestResets()
    {
        await using var f = new AlertFixture();
        f.Reader.Page = (after, _) =>
        {
            var number = after is null ? 1 : 1002 - (int)after.AfterOrdinal;
            return Task.FromResult(AlertFixture.Page(number, 1001 - number, more: true));
        };
        await f.Model.LoadCommand.ExecuteAsync();
        for (var index = 0; index < 140; index++) await f.Model.OlderCommand.ExecuteAsync();
        Assert.Equal(141, f.Model.State.PageNumber);
        Assert.True(f.Model.State.BackHistoryLimited);
        Assert.Single(f.Model.State.Rows);
        for (var index = 0; index < 128; index++) await f.Model.NewerCommand.ExecuteAsync();
        Assert.Equal(13, f.Model.State.PageNumber);
        Assert.False(f.Model.State.HasNewer);
        Assert.False(f.Model.NewerCommand.CanExecute(null));
        Assert.True(f.Model.OlderCommand.CanExecute(null));
        await f.Model.LatestCommand.ExecuteAsync();
        Assert.Equal(1, f.Model.State.PageNumber);
        Assert.False(f.Model.State.BackHistoryLimited);
        Assert.Null(f.Reader.Pages.Last().Cursor);
        Assert.All(f.Reader.Pages, request => Assert.Equal(25, request.Size));
    }

    [Fact]
    public async Task FailedOlderReadRetainsCursorSoRetryDoesNotSkipAnyTuple()
    {
        await using var f = new AlertFixture();
        f.Reader.Page = (_, _) => Task.FromResult(AlertFixture.Page(more: true));
        await f.Model.LoadCommand.ExecuteAsync();
        f.Reader.Page = (_, _) => throw new AlertHistoryReadException();
        await f.Model.OlderCommand.ExecuteAsync();
        Assert.True(f.Model.State.IsStale);
        f.Reader.Page = (_, _) => Task.FromResult(AlertFixture.Page(2, 999));
        await f.Model.OlderCommand.ExecuteAsync();
        var requests = f.Reader.Pages.ToArray();
        Assert.Equal(requests[1].Cursor, requests[2].Cursor);
        Assert.Equal(2, f.Model.State.PageNumber);
        Assert.False(f.Model.State.IsStale);
        Assert.Null(f.Model.State.MessageKey);
    }

    [Fact]
    public async Task RecipientAndRecordPagesReplaceBoundedRowsAndKeepTheirExactEventScopedCursors()
    {
        await using var f = new AlertFixture();
        var eventId = AlertFixture.Id(1);
        var recipient = new AlertHistoryRecipient(AlertFixture.Id(10), 1, SnuWatcher.Core.OpeningReason.IncreasedSeats, "<literal label>");
        var recipientCursor = new AlertRecipientCursor(eventId, recipient.SubscriptionId, 1);
        var recipientRequests = new List<AlertRecipientCursor?>();
        f.Reader.Recipients = (id, after, _) =>
        {
            recipientRequests.Add(after);
            return Task.FromResult<AlertRecipientPage?>(new(id, AlertFixture.Stamp,
                [after is null ? recipient : new(AlertFixture.Id(11), 2, SnuWatcher.Core.OpeningReason.InitialAvailability, null)],
                after is null, after is null ? recipientCursor : null));
        };
        var result = new AlertEventResult(5, SnuWatcher.Application.Persistence.DeliveryStatus.Pending, 0, AlertFixture.Now);
        var attemptCursor = new AlertAttemptCursor(eventId, AlertAttemptOrigin.EventResult, 5);
        var attemptRequests = new List<AlertAttemptCursor?>();
        f.Reader.Attempts = (id, after, _) =>
        {
            attemptRequests.Add(after);
            return Task.FromResult<AlertAttemptPage?>(new(id, AlertFixture.Stamp,
                [new(id, null, after is null ? result : new(4, result.Status, 0, AlertFixture.Now), true)],
                after is null, after is null ? attemptCursor : null));
        };
        await f.Model.LoadCommand.ExecuteAsync();
        Assert.True(await f.Model.SelectEventAsync(eventId));
        Assert.Equal("<literal label>", Assert.Single(f.Model.State.Recipients).WatchLabel);
        await f.Model.RecipientNextCommand.ExecuteAsync();
        await f.Model.AttemptNextCommand.ExecuteAsync();
        Assert.Equal(recipientCursor, recipientRequests[1]);
        Assert.Equal(attemptCursor, attemptRequests[1]);
        Assert.Single(f.Model.State.Recipients);
        Assert.Single(f.Model.State.Attempts);
        Assert.Equal(2, f.Model.State.RecipientPageNumber);
        Assert.Equal(2, f.Model.State.AttemptPageNumber);
        await f.Model.RecipientPreviousCommand.ExecuteAsync();
        await f.Model.AttemptPreviousCommand.ExecuteAsync();
        Assert.Null(recipientRequests[2]);
        Assert.Null(attemptRequests[2]);
        await f.Model.RecipientFirstCommand.ExecuteAsync();
        await f.Model.AttemptFirstCommand.ExecuteAsync();
        Assert.Equal(1, f.Model.State.RecipientPageNumber);
        Assert.Equal(1, f.Model.State.AttemptPageNumber);
    }

    [Fact]
    public async Task OnlyCurrentVisibleIdentityCanSelectAndFixedActionsUseFreshDetailAuthority()
    {
        await using var f = new AlertFixture();
        await f.Model.LoadCommand.ExecuteAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Model.SelectEventAsync(AlertFixture.Id(9)));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Model.SelectEventAsync(Guid.Empty));
        await f.Model.SelectEventAsync(AlertFixture.Id(1));
        await f.Model.OpenRegistrationCommand.ExecuteAsync();
        await f.Model.CopyIdentifierCommand.ExecuteAsync();
        Assert.Equal(StudentDestination.Registration, f.Actions.Opened);
        Assert.Equal(AlertFixture.Key, f.Actions.Copied);
        await f.Model.SelectEventAsync(null);
        Assert.False(f.Model.CopyIdentifierCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullOrCrossEventDetailCannotInventAnEmptySuccessfulSelection(bool missing)
    {
        await using var f = new AlertFixture();
        await f.Model.LoadCommand.ExecuteAsync();
        f.Reader.Details = (_, _) => Task.FromResult(missing ? null : AlertFixture.Item(2));
        Assert.False(await f.Model.SelectEventAsync(AlertFixture.Id(1)));
        Assert.Null(f.Model.State.Details);
        Assert.Equal(missing ? "Ui.AlertEventUnavailable" : "Ui.AlertHistoryUnavailable", f.Model.State.DetailsMessageKey);
        Assert.False(f.Model.OpenRegistrationCommand.CanExecute(null));
    }
}
