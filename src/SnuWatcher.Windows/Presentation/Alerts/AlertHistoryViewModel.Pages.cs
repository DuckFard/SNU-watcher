using System.Collections.Immutable;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Alerts;

public sealed partial class AlertHistoryViewModel
{
    private UiAsyncCommand PageCommand(AlertPageMove move) => new(token => ReadPageAsync(move, token), dispatcher, Failure,
        () => !stopping && (move != AlertPageMove.Load || !State.IsLoaded)
            && (move != AlertPageMove.Older || State.HasOlder) && (move != AlertPageMove.Newer || State.HasNewer));

    private async Task ReadPageAsync(AlertPageMove move, CancellationToken token)
    {
        await pageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (move == AlertPageMove.Load && State.IsLoaded) return;
            var before = Volatile.Read(ref navigation);
            if (move == AlertPageMove.Older && before.Next is null || move == AlertPageMove.Newer && before.Previous.IsEmpty) return;
            var cursor = before.Request(move);
            var page = await reader.ReadPageAsync(cursor, 25, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            AlertContinuationValidation.History(page, cursor);
            var next = before.Complete(move, cursor, page.NextCursor);
            await dispatcher.InvokeAsync(() =>
            {
                if (stopping) return;
                var selectionBefore = selectionGeneration;
                Volatile.Write(ref navigation, next);
                Model.Replace(State with
                {
                    Rows = page.Items.Select(item => new AlertHistoryRow(item)).ToImmutableArray(), IsLoaded = true,
                    IsStale = false, LastSuccessfulReadAtUtc = page.Stamp.ReadAtUtc, PageNumber = next.Number,
                    HasOlder = page.HasMore, HasNewer = !next.Previous.IsEmpty, BackHistoryLimited = next.Limited, MessageKey = null,
                });
                if (selectionGeneration == selectionBefore && State.SelectedEventId is { } selected)
                    RequestSelection(State.Rows.Any(row => row.EventId == selected) ? selected : null);
                NotifyCommands();
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Report();
            await dispatcher.InvokeAsync(() =>
            {
                if (!stopping) Model.Replace(State with { MessageKey = "Ui.AlertHistoryUnavailable", IsStale = State.IsLoaded });
            }).ConfigureAwait(false);
        }
        finally { pageGate.Release(); }
    }
}
