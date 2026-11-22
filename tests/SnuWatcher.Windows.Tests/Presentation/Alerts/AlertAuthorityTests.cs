using System.ComponentModel;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Alerts;

public sealed class AlertAuthorityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalContinuationCannotChangeHighWaterOrRepeatRequestedTuple(bool highWater)
    {
        await using var f = new AlertFixture();
        f.Reader.Page = (_, _) => Task.FromResult(AlertFixture.Page(more: true));
        await f.Model.LoadCommand.ExecuteAsync();
        f.Reader.Page = (_, _) => Task.FromResult(highWater
            ? new AlertHistoryPage(AlertFixture.Stamp, 999, [AlertFixture.Item(2, 999)], false, null)
            : AlertFixture.Page(more: true));
        await f.Model.OlderCommand.ExecuteAsync();
        Assert.True(f.Model.State.IsStale);
        Assert.Equal(1, f.Model.State.PageNumber);
        Assert.Equal(AlertFixture.Id(1), Assert.Single(f.Model.State.Rows).EventId);
    }

    [Fact]
    public async Task DetailContinuationsCannotRepeatAssociationsOrRecordSourcePositions()
    {
        await using var f = new AlertFixture();
        var id = AlertFixture.Id(1);
        var recipient = new AlertHistoryRecipient(AlertFixture.Id(2), 1, OpeningReason.IncreasedSeats, "Label");
        f.Reader.Recipients = (_, _, _) => Task.FromResult<AlertRecipientPage?>(new(id, AlertFixture.Stamp, [recipient], true, new(id, recipient.SubscriptionId, 1)));
        var result = new AlertEventResult(5, DeliveryStatus.Pending, 0, AlertFixture.Now);
        f.Reader.Attempts = (_, _, _) => Task.FromResult<AlertAttemptPage?>(new(id, AlertFixture.Stamp, [new(id, null, result, true)], true, new(id, AlertAttemptOrigin.EventResult, 5)));
        await f.Model.LoadCommand.ExecuteAsync();
        await f.Model.SelectEventAsync(id);
        await f.Model.RecipientNextCommand.ExecuteAsync();
        await f.Model.AttemptNextCommand.ExecuteAsync();
        Assert.True(f.Model.State.RecipientsStale);
        Assert.True(f.Model.State.AttemptsStale);
        Assert.Equal(1, f.Model.State.RecipientPageNumber);
        Assert.Equal(1, f.Model.State.AttemptPageNumber);
    }

    [Fact]
    public async Task ReentrantActionCancellationCannotOverwriteTheNewestSelectionIntent()
    {
        await using var f = new AlertFixture();
        f.Reader.Page = (_, _) => Task.FromResult(new AlertHistoryPage(AlertFixture.Stamp, 1000,
            [AlertFixture.Item(1, 1000), AlertFixture.Item(2, 999), AlertFixture.Item(3, 998)], false, null));
        await f.Model.LoadCommand.ExecuteAsync();
        await f.Model.SelectEventAsync(AlertFixture.Id(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Actions.CopyHandler = (_, _) => { entered.TrySetResult(); return release.Task; };
        var copying = f.Model.CopyIdentifierCommand.ExecuteAsync();
        Task<bool>? latest = null;
        var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = false;
        PropertyChangedEventHandler observer = (_, _) =>
        {
            if (!once && f.Model.CopyIdentifierCommand.IsCancellationRequested)
            { once = true; latest = f.Model.SelectEventAsync(AlertFixture.Id(3)); reentered.TrySetResult(); }
        };
        try
        {
            await entered.Task.WaitAsync(Timeout);
            f.Model.CopyIdentifierCommand.PropertyChanged += observer;
            var middle = f.Model.SelectEventAsync(AlertFixture.Id(2));
            await reentered.Task.WaitAsync(Timeout);
            Assert.NotNull(latest);
            Assert.True(await latest!.WaitAsync(Timeout));
            Assert.Equal(AlertFixture.Id(3), f.Model.State.Details!.EventId);
            try { await middle; } catch (OperationCanceledException) { }
        }
        finally { f.Model.CopyIdentifierCommand.PropertyChanged -= observer; release.TrySetResult(); await copying.WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ClipboardFailureDoesNotInventAHistoryReadFailureOrStaleRows()
    {
        await using var f = new AlertFixture();
        await f.Model.LoadCommand.ExecuteAsync();
        await f.Model.SelectEventAsync(AlertFixture.Id(1));
        f.Actions.CopyHandler = (_, _) => throw new IOException("private clipboard detail");
        await f.Model.CopyIdentifierCommand.ExecuteAsync();
        Assert.False(f.Model.State.IsStale);
        Assert.Equal("Ui.CommandFailed", f.Model.State.MessageKey);
    }
}
