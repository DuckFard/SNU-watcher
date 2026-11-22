using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;

namespace SnuWatcher.Windows.Presentation.Watches;

public sealed partial class WatchListViewModel
{
    private Task<bool> RequestSelection(Guid id, bool allowDuringMutation)
    {
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Watch selection requires its dispatcher.");
        if (stopping) throw new ObjectDisposedException(nameof(WatchListViewModel));
        if (!allowDuringMutation && State.IsMutating) throw new InvalidOperationException("Finish the current watch action first.");
        selectionReady?.TrySetCanceled(new CancellationToken(true));
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref selectionReady, waiting);
        if (stopping)
        {
            waiting.TrySetCanceled(new CancellationToken(true));
            throw new ObjectDisposedException(nameof(WatchListViewModel));
        }
        selectionGeneration++;
        detailCommand.Cancel();
        var changed = State.SelectedId != id;
        Model.Replace(State with
        {
            SelectedId = id, Details = changed ? null : State.Details,
            SelectedMemberKey = changed ? null : State.SelectedMemberKey,
            SelectionPending = true, MessageKey = null,
        });
        detailCommand.Execute(null);
        return waiting.Task;
    }

    private async Task<bool> ReloadSelectionAsync(Guid id, CancellationToken token)
    {
        Task<bool>? waiting = null;
        await dispatcher.InvokeAsync(() =>
        {
            if (!stopping && State.SelectedId == id) waiting = RequestSelection(id, allowDuringMutation: true);
        }).ConfigureAwait(false);
        if (waiting is null) return true;
        try { return await waiting.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A newer selection owns the panel; superseding its old waiter is not an action failure.
            return true;
        }
    }

    private void OnDetailAvailabilityChanged(object? sender, EventArgs args)
    {
        if (!stopping && detailCommand.CanExecute(null)) detailCommand.Execute(null);
    }

    private async Task LoadSelectedDetailsCoreAsync(CancellationToken token)
    {
        var generation = Interlocked.Read(ref selectionGeneration);
        var id = State.SelectedId;
        if (id is null) return;
        try
        {
            var details = await reader.ReadWatchDetailsAsync(id.Value, token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (stopping || generation != selectionGeneration || State.SelectedId != id) return;
                var projected = details is null ? null : ProjectDetails(details, clock.GetUtcNow(), appliedTarget());
                var previous = State.Details?.Source.Subscription;
                var resetEditor = previous?.Id != details?.Subscription.Id || previous?.DefinitionRevision != details?.Subscription.DefinitionRevision;
                var selectedMember = projected?.Members.FirstOrDefault(member => member.Key == State.SelectedMemberKey)
                    ?? projected?.Members.FirstOrDefault();
                var waiting = selectionReady;
                settledSelectionGeneration = generation;
                Model.Replace(State with
                {
                    Details = projected, SelectedMemberKey = selectedMember?.Key, SelectionPending = false,
                    RenameText = resetEditor ? details?.Subscription.Label ?? "" : State.RenameText,
                    NoExpiry = resetEditor ? details?.Subscription.Expiry is null : State.NoExpiry,
                    ExpiryDate = resetEditor ? details?.Subscription.Expiry?.LocalDate.ToDateTime(TimeOnly.MinValue) : State.ExpiryDate,
                    MessageKey = details is null ? "Ui.WatchDetailsUnavailable" : null,
                });
                waiting?.TrySetResult(projected is not null);
                NotifyCommands();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (generation != selectionGeneration || stopping) return;
                var waiting = selectionReady;
                settledSelectionGeneration = generation;
                Model.Replace(State with { SelectionPending = false, MessageKey = "Ui.CommandFailed" });
                waiting?.TrySetResult(false);
            }).ConfigureAwait(false);
        }
    }

    private void DetailFailure(string code)
    {
        if (stopping) return;
        var waiting = selectionReady;
        settledSelectionGeneration = selectionGeneration;
        Model.Replace(State with { SelectionPending = false, MessageKey = "Ui.CommandFailed" });
        waiting?.TrySetResult(false);
    }

    private void ClearSelection()
    {
        selectionGeneration++;
        settledSelectionGeneration = selectionGeneration;
        selectionReady?.TrySetCanceled(new CancellationToken(true));
        detailCommand.Cancel();
        Model.Replace(State with { SelectedId = null, Details = null, SelectedMemberKey = null,
            SelectionPending = false, RenameText = "", NoExpiry = true, ExpiryDate = null });
    }
}
