using System.Collections.Immutable;
using SnuWatcher.Application.Presentation.Alerts;

namespace SnuWatcher.Windows.Presentation.Alerts;

public sealed partial class AlertHistoryViewModel
{
    private Task<bool> RequestSelection(Guid? id)
    {
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Alert selection requires its dispatcher.");
        ObjectDisposedException.ThrowIf(stopping, this);
        if (id is not null && !State.Rows.Any(row => row.EventId == id)) throw new ArgumentException("Select a current visible event.", nameof(id));
        selectionReady?.TrySetCanceled(new CancellationToken(true));
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref selectionReady, waiting);
        var generation = ++selectionGeneration;
        recipientNavigation = AlertNavigation<AlertRecipientCursor>.Empty;
        attemptNavigation = AlertNavigation<AlertAttemptCursor>.Empty;
        Model.Replace(State with
        {
            SelectedEventId = id, SelectionPending = id is not null, Details = null, DetailsReadAtUtc = null,
            DetailsStale = false, DetailsMessageKey = null, Recipients = [], Attempts = [], RecipientPageNumber = 1,
            AttemptPageNumber = 1, HasNextRecipients = false, HasPreviousRecipients = false, HasNextAttempts = false,
            HasPreviousAttempts = false, RecipientMessageKey = null, AttemptMessageKey = null, RecipientsStale = false, AttemptsStale = false,
        });
        if (generation != selectionGeneration) return waiting.Task;
        // Publish intent before observable cancellation callbacks can reenter selection.
        foreach (var command in new[] { selectionCommand, RecipientFirstCommand, RecipientPreviousCommand, RecipientNextCommand,
            AttemptFirstCommand, AttemptPreviousCommand, AttemptNextCommand, OpenRegistrationCommand, CopyIdentifierCommand })
        {
            if (generation != selectionGeneration) return waiting.Task;
            command.Cancel();
        }
        if (generation != selectionGeneration) return waiting.Task;
        if (id is null) { settledSelectionGeneration = generation; waiting.TrySetResult(true); }
        else selectionRequests.Writer.TryWrite(generation);
        NotifyCommands();
        return waiting.Task;
    }

    private async Task RunSelectionRequestsAsync()
    {
        await foreach (var generation in selectionRequests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (stopping || generation != Interlocked.Read(ref selectionGeneration) || State.SelectedEventId is null) continue;
            try
            {
                await selectionCommand.ExecuteAsync().ConfigureAwait(false);
                if (stopping) continue;
                await dispatcher.InvokeAsync(() =>
                {
                    if (!stopping && generation == selectionGeneration && State.SelectionPending)
                        SelectionFailure("ui_alert_history_failed");
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Report();
                if (generation == Interlocked.Read(ref selectionGeneration)) Volatile.Read(ref selectionReady)?.TrySetResult(false);
            }
        }
    }

    private async Task ReadSelectionAsync(CancellationToken token)
    {
        var generation = Interlocked.Read(ref selectionGeneration);
        var id = State.SelectedEventId;
        if (id is null) return;
        try
        {
            // Turn a synchronous port throw into an owned faulted task before starting sibling reads.
            var detailsTask = ReadOwnedAsync(() => reader.ReadDetailsAsync(id.Value, token));
            var recipientsTask = ReadOwnedAsync(() => reader.ReadRecipientsAsync(id.Value, null, 25, token));
            var attemptsTask = ReadOwnedAsync(() => reader.ReadAttemptsAsync(id.Value, null, 25, token));
            await Task.WhenAll(detailsTask, recipientsTask, attemptsTask).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var details = await detailsTask.ConfigureAwait(false);
            var recipients = await recipientsTask.ConfigureAwait(false);
            var attempts = await attemptsTask.ConfigureAwait(false);
            if (details is not null && details.EventId != id || recipients is not null && (recipients.EventId != id || recipients.Items.Length > 25)
                || attempts is not null && (attempts.EventId != id || attempts.Items.Length > 25)) throw new InvalidDataException("Invalid alert detail identity.");
            var completedAt = clock.GetUtcNow();
            await dispatcher.InvokeAsync(() =>
            {
                if (stopping || generation != selectionGeneration || id != State.SelectedEventId) return;
                var waiting = selectionReady;
                settledSelectionGeneration = generation;
                recipientNavigation = AlertNavigation<AlertRecipientCursor>.Empty with { Next = recipients?.NextCursor };
                attemptNavigation = AlertNavigation<AlertAttemptCursor>.Empty with { Next = attempts?.NextCursor };
                Model.Replace(State with
                {
                    Details = details is null ? null : new(details), DetailsReadAtUtc = details is null ? null : completedAt,
                    SelectionPending = false, DetailsMessageKey = details is null ? "Ui.AlertEventUnavailable" : null,
                    Recipients = details is null || recipients is null ? [] : recipients.Items.Select(item => new AlertRecipientRow(item)).ToImmutableArray(),
                    Attempts = details is null || attempts is null ? [] : attempts.Items.Select(item => new AlertRecordRow(item)).ToImmutableArray(),
                    HasNextRecipients = details is not null && recipients?.HasMore == true,
                    HasNextAttempts = details is not null && attempts?.HasMore == true,
                    RecipientMessageKey = recipients is null ? "Ui.AlertEventUnavailable" : null,
                    AttemptMessageKey = attempts is null ? "Ui.AlertEventUnavailable" : null,
                    RecipientsStale = recipients is null, AttemptsStale = attempts is null,
                });
                waiting?.TrySetResult(details is not null);
                NotifyCommands();
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Report();
            await dispatcher.InvokeAsync(() =>
            {
                if (!stopping && generation == selectionGeneration) SelectionFailure("ui_alert_history_failed");
            }).ConfigureAwait(false);
        }
    }

    private static async Task<T> ReadOwnedAsync<T>(Func<Task<T>> read) => await read().ConfigureAwait(false);

    private void SelectionFailure(string code)
    {
        Report();
        if (stopping) return;
        var waiting = selectionReady;
        settledSelectionGeneration = selectionGeneration;
        Model.Replace(State with { SelectionPending = false, DetailsStale = true, DetailsMessageKey = "Ui.AlertHistoryUnavailable" });
        waiting?.TrySetResult(false);
        NotifyCommands();
    }
}
