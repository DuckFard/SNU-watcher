using System.Collections.Immutable;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Alerts;

public sealed partial class AlertHistoryViewModel
{
    private UiAsyncCommand DetailPageCommand(bool recipients, AlertPageMove move) => new(
        token => ReadDetailPageAsync(recipients, move, token), dispatcher, Failure,
        () => !stopping && !State.SelectionPending && State.Details is not null
            && (move != AlertPageMove.Older || (recipients ? State.HasNextRecipients : State.HasNextAttempts))
            && (move != AlertPageMove.Newer || (recipients ? State.HasPreviousRecipients : State.HasPreviousAttempts)));

    private async Task ReadDetailPageAsync(bool recipients, AlertPageMove move, CancellationToken token)
    {
        var gate = recipients ? recipientGate : attemptGate;
        await gate.WaitAsync(token).ConfigureAwait(false);
        var generation = Interlocked.Read(ref selectionGeneration);
        var id = State.SelectedEventId;
        try
        {
            if (id is null) return;
            if (recipients)
            {
                var before = recipientNavigation;
                if (move == AlertPageMove.Older && before.Next is null || move == AlertPageMove.Newer && before.Previous.IsEmpty) return;
                var cursor = before.Request(move);
                var page = await reader.ReadRecipientsAsync(id.Value, cursor, 25, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                AlertContinuationValidation.Recipients(page, id.Value, cursor);
                await dispatcher.InvokeAsync(() =>
                {
                    if (stopping || generation != selectionGeneration || id != State.SelectedEventId) return;
                    recipientNavigation = before.Complete(move, cursor, page.NextCursor);
                    Model.Replace(State with { Recipients = page.Items.Select(item => new AlertRecipientRow(item)).ToImmutableArray(),
                        RecipientPageNumber = recipientNavigation.Number, HasNextRecipients = page.HasMore,
                        HasPreviousRecipients = !recipientNavigation.Previous.IsEmpty, RecipientsStale = false, RecipientMessageKey = null });
                    NotifyCommands();
                }, token).ConfigureAwait(false);
            }
            else await ReadAttemptPageAsync(id.Value, generation, move, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Report();
            await dispatcher.InvokeAsync(() =>
            {
                if (stopping || generation != selectionGeneration) return;
                Model.Replace(recipients ? State with { RecipientsStale = true, RecipientMessageKey = "Ui.AlertHistoryUnavailable" }
                    : State with { AttemptsStale = true, AttemptMessageKey = "Ui.AlertHistoryUnavailable" });
            }).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task ReadAttemptPageAsync(Guid id, long generation, AlertPageMove move, CancellationToken token)
    {
        var before = attemptNavigation;
        if (move == AlertPageMove.Older && before.Next is null || move == AlertPageMove.Newer && before.Previous.IsEmpty) return;
        var cursor = before.Request(move);
        var page = await reader.ReadAttemptsAsync(id, cursor, 25, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        AlertContinuationValidation.Attempts(page, id, cursor);
        await dispatcher.InvokeAsync(() =>
        {
            if (stopping || generation != selectionGeneration || id != State.SelectedEventId) return;
            attemptNavigation = before.Complete(move, cursor, page.NextCursor);
            Model.Replace(State with { Attempts = page.Items.Select(item => new AlertRecordRow(item)).ToImmutableArray(),
                AttemptPageNumber = attemptNavigation.Number, HasNextAttempts = page.HasMore,
                HasPreviousAttempts = !attemptNavigation.Previous.IsEmpty, AttemptsStale = false, AttemptMessageKey = null });
            NotifyCommands();
        }, token).ConfigureAwait(false);
    }
}
