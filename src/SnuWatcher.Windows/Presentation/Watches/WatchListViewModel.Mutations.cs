using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Watches;

public sealed partial class WatchListViewModel
{
    private enum WatchEdit { Pause, Resume, Remove, Rename, Expiry }

    private UiAsyncCommand MutationCommand(WatchEdit edit) => new(token => ChangeCoreAsync(edit, token), dispatcher, CommandFailure, () =>
    {
        if (stopping || State.IsLoading || State.IsMutating || State.SelectionPending || State.Details is not { } details) return false;
        if (edit == WatchEdit.Pause) return details.Health.EffectiveWatchStatus == WatchStatus.Active;
        return edit != WatchEdit.Resume || details.Source.Subscription.Status is WatchStatus.Paused or WatchStatus.Expired
            && details.Source.Subscription.Expiry?.IsExpiredAt(clock.GetUtcNow()) != true;
    });

    private async Task ChangeCoreAsync(WatchEdit edit, CancellationToken token)
    {
        if (!await mutationGate.WaitAsync(0, token).ConfigureAwait(false)) return;
        var receiptKnown = false;
        try
        {
            var captured = State;
            if (captured.Details is null) return;
            SubscriptionChange command;
            try { command = Change(edit, captured); }
            catch (ArgumentException)
            {
                await dispatcher.InvokeAsync(() => SetMessage(edit == WatchEdit.Expiry ? "Ui.ExpiryInvalid" : "Ui.WatchLabelInvalid")).ConfigureAwait(false);
                return;
            }
            await commands.ChangeAsync(command, token).ConfigureAwait(false);
            receiptKnown = true;
            if (edit == WatchEdit.Remove) await dispatcher.InvokeAsync(ClearSelection).ConfigureAwait(false);
            var pageLoaded = await ReadPageCoreAsync(PageMove.Reload, token, reloadDetails: false).ConfigureAwait(false);
            var detailsLoaded = edit == WatchEdit.Remove || await ReloadSelectionAsync(command.Id, token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => SetMessage(pageLoaded && detailsLoaded
                ? "Ui.WatchSaved" : "Ui.WatchSavedReloadFailed")).ConfigureAwait(false);
        }
        catch (StoreVersionConflictException)
        {
            var selected = State.SelectedId;
            await ReadPageCoreAsync(PageMove.Reload, token, reloadDetails: false).ConfigureAwait(false);
            if (selected is { } id) await ReloadSelectionAsync(id, token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => SetMessage("Ui.WatchConflict")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await dispatcher.InvokeAsync(() => SetMessage(receiptKnown ? "Ui.WatchSavedReloadFailed" : "Ui.SavePending")).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        { await dispatcher.InvokeAsync(() => SetMessage(receiptKnown ? "Ui.WatchSavedReloadFailed" : "Ui.CommandFailed")).ConfigureAwait(false); }
        finally { mutationGate.Release(); }
    }

    private SubscriptionChange Change(WatchEdit edit, WatchListState captured)
    {
        var definition = captured.Details!.Source.Subscription;
        var now = clock.GetUtcNow();
        return edit switch
        {
            WatchEdit.Pause => new PauseSubscription(definition.Id, definition.DefinitionRevision, now),
            WatchEdit.Resume => new ResumeSubscription(definition.Id, definition.DefinitionRevision, now),
            WatchEdit.Remove => new RemoveSubscription(definition.Id, definition.DefinitionRevision, now),
            WatchEdit.Rename => new RenameSubscription(definition.Id, definition.DefinitionRevision, now, captured.RenameText),
            _ => new SetSubscriptionExpiry(definition.Id, definition.DefinitionRevision, now,
                captured.NoExpiry ? null : WatchExpiry.FromLocalDate(DateOnly.FromDateTime(captured.ExpiryDate
                    ?? throw new ArgumentException("An expiry date is required.")))),
        };
    }

    private bool CanRefresh() => !stopping && !State.IsLoading && !State.IsMutating && !State.SelectionPending
        && State.Details is not null && State.SelectedMemberKey is not null;

    private async Task RefreshMemberCoreAsync(CancellationToken token)
    {
        var captured = State;
        if (captured.SelectedId is not { } id || captured.SelectedMemberKey is not { } key) return;
        try
        {
            var result = await commands.RefreshAsync(key, token).ConfigureAwait(false);
            await ReloadSelectionAsync(id, token).ConfigureAwait(false);
            if (result?.Disposition != Application.Monitoring.Hosting.OwnedRefreshDisposition.Completed
                || result.MonitorResult?.Disposition != Application.Monitoring.Scheduling.WatchRefreshDisposition.Completed)
                await dispatcher.InvokeAsync(() => SetMessage("Ui.RefreshDeferred")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { await dispatcher.InvokeAsync(() => SetMessage("Ui.RefreshDeferred")).ConfigureAwait(false); }
    }
}
