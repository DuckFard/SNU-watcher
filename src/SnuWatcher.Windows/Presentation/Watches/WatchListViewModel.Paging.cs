using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Health;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Watches;

public sealed partial class WatchListViewModel
{
    private enum PageMove { First, Previous, Next, Reload }
    private sealed record Navigation(WatchPageCursor? Cursor, ImmutableArray<WatchPageCursor?> History,
        long Number, bool HistoryLimited, WatchPageSnapshot? Page);

    private UiAsyncCommand PageCommand(PageMove move) => new(token => ReadPageCoreAsync(move, token,
            recordReload: move == PageMove.Reload), dispatcher, CommandFailure,
        () => !stopping && !State.IsMutating && (move == PageMove.Reload || !State.IsLoading)
            && (move != PageMove.Next || State.HasNext) && (move != PageMove.Previous || State.HasPrevious));

    private async Task<bool> ReadPageCoreAsync(PageMove move, CancellationToken token, bool reloadDetails = true, bool recordReload = false)
    {
        await pageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = Volatile.Read(ref navigation);
            var cursor = move switch
            {
                PageMove.First => null, PageMove.Next => before.Page?.NextCursor,
                PageMove.Previous when !before.History.IsEmpty => before.History[^1], _ => before.Cursor,
            };
            var result = await reader.ReadWatchPageAsync(cursor, 25, token).ConfigureAwait(false);
            var restarted = result.Disposition == WatchPageReadDisposition.RestartRequired;
            if (restarted) result = await reader.ReadWatchPageAsync(null, 25, token).ConfigureAwait(false);
            var page = result.Page ?? throw new InvalidOperationException("A restarted watch page was unavailable.");
            var next = Navigate(before, move, cursor, page, restarted);
            Guid? selected = null;
            var published = false;
            await dispatcher.InvokeAsync(() =>
            {
                if (stopping) return;
                var rows = ProjectRows(page, clock.GetUtcNow(), appliedTarget());
                var pageVersion = checked(SuccessfulPageReadVersion + 1);
                var reloadVersion = recordReload ? checked(SuccessfulReloadVersion + 1) : SuccessfulReloadVersion;
                Volatile.Write(ref navigation, next);
                Model.Replace(State with
                {
                    Rows = rows, IsLoaded = true,
                    PageNumber = next.Number, HasPrevious = !next.History.IsEmpty, HasNext = page.HasMore,
                    BackHistoryLimited = next.HistoryLimited,
                    PagingNoticeKey = restarted ? "Ui.PageRestarted" : next.HistoryLimited ? "Ui.PageHistoryLimit" : null,
                    MessageKey = null,
                });
                Interlocked.Exchange(ref successfulPageReadVersion, pageVersion);
                if (recordReload) Interlocked.Exchange(ref successfulReloadVersion, reloadVersion);
                published = true;
                selected = State.SelectedId;
                NotifyCommands();
            }).ConfigureAwait(false);
            if (reloadDetails && selected is { } id) await ReloadSelectionAsync(id, token).ConfigureAwait(false);
            return published;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await dispatcher.InvokeAsync(() => SetMessage("Ui.CommandFailed")).ConfigureAwait(false);
            return false;
        }
        finally { pageGate.Release(); }
    }

    private static Navigation Navigate(Navigation before, PageMove move, WatchPageCursor? cursor, WatchPageSnapshot page, bool restarted)
    {
        if (restarted || move == PageMove.First) return new(null, [], 1, false, page);
        if (move == PageMove.Next)
        {
            var history = before.History.Add(before.Cursor);
            var limited = before.HistoryLimited || history.Length > 64;
            if (history.Length > 64) history = history.RemoveAt(0);
            return new(cursor, history, checked(before.Number + 1), limited, page);
        }
        if (move == PageMove.Previous)
            return new(cursor, before.History.RemoveAt(before.History.Length - 1), before.Number - 1, before.HistoryLimited, page);
        return before with { Page = page };
    }

    private ImmutableArray<WatchRow> ProjectRows(WatchPageSnapshot page, DateTimeOffset now, TimeSpan target) =>
        page.Items.Select(item => new WatchRow(item,
            item.SingleSection is null ? null : SectionHealthProjector.Project(item.SingleSection, now, target),
            item.Expiry?.IsExpiredAt(now) == true ? WatchStatus.Expired : item.StoredStatus, GetTermLabel(item.Term))).ToImmutableArray();

    private string? GetTermLabel(AcademicTermIdentity term)
    {
        try
        {
            var label = termLabel?.Invoke(term);
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch (Exception) { System.Diagnostics.Trace.WriteLine("ui_watch_term_label_failed"); return null; }
    }

    private static WatchDetailsView ProjectDetails(WatchDetailsSnapshot details, DateTimeOffset now, TimeSpan target)
    {
        var health = GroupHealthProjector.Project(details.Subscription, details.Sections, now, target);
        return new(details, health, health.Members.Select(member => new WatchMemberRow(member)));
    }
}
