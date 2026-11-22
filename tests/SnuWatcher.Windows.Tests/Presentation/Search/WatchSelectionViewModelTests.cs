using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Tests.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Search;

/// <summary>Stable create identity, expiry and actual receipt ownership.</summary>
public sealed class WatchSelectionViewModelTests
{
    /// <summary>A completed confirmation cannot silently re-enable an old command when its input bindings change.</summary>
    [Fact]
    public async Task SuccessfulConfirmationRemainsFrozenUntilAnExplicitNewSelection()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var watches = new SearchWatches();
        await using var view = new WatchSelectionViewModel(watches, dispatcher, new SearchClock());
        await dispatcher.InvokeAsync(() => { view.ReplaceSelection([SearchQueries.Row(Key())]); view.Label = "First"; });
        await view.SaveCommand.ExecuteAsync();
        await dispatcher.InvokeAsync(() => { view.Label = "Changed"; view.InitialNoticeEnabled = false; view.UseExpiry = true; view.ExpiryDate = new DateTime(2026, 11, 3); });
        Assert.Equal(WatchSelectionStatus.Saved, view.Model.State.Status);
        Assert.Equal("First", view.Label);
        Assert.False(view.SaveCommand.CanExecute(null));
        await view.SaveCommand.ExecuteAsync();
        Assert.Single(watches.Creates);
        await dispatcher.InvokeAsync(() => { view.ReplaceSelection([SearchQueries.Row(Key())]); view.Label = "Second explicit watch"; });
        await view.SaveCommand.ExecuteAsync();
        Assert.Equal(2, watches.Creates.Count);
        Assert.NotEqual(watches.Creates[0].Id, watches.Creates[1].Id);
        Assert.Equal("Second explicit watch", watches.Creates[1].Label);
    }

    /// <summary>Confirmation preserves full keys and includes the selected Korean final day.</summary>
    [Fact]
    public async Task ExplicitSelectionUsesFullKeysAndInclusiveKoreanDateAndPublishesReceiptBeforeObserver()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var watches = new SearchWatches();
        await using var view = new WatchSelectionViewModel(watches, dispatcher, new SearchClock());
        await dispatcher.InvokeAsync(() =>
        {
            view.ReplaceSelection([SearchQueries.Row(Key()), SearchQueries.Row(Key("F33.107", "002"))]);
            view.Label = "My chosen sections"; view.UseExpiry = true; view.ExpiryDate = new DateTime(2026, 11, 3);
        });
        var observedReceipt = false;
        view.WatchCreated += receipt => { observedReceipt = ReferenceEquals(receipt, view.Model.State.Created); throw new IOException("private observer"); };
        await view.SaveCommand.ExecuteAsync();
        var command = Assert.Single(watches.Creates);
        Assert.Equal(2, command.Sections.Length);
        Assert.Equal(SnuWatcher.Application.Persistence.SubscriptionKind.Group, command.Kind);
        Assert.True(command.InitialNoticeEnabled);
        Assert.Equal(new DateOnly(2026, 11, 3), command.Expiry!.LocalDate);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T15:00:00Z"), command.Expiry.ExclusiveUtc);
        Assert.True(observedReceipt);
        Assert.Equal(WatchSelectionStatus.Saved, view.Model.State.Status);
    }

    /// <summary>Lost returns retry the same immutable create instead of inventing another watch.</summary>
    [Fact]
    public async Task AmbiguousFailureRetainsExactCreateIdentityAndFactsAcrossRetry()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var watches = new SearchWatches();
        var first = true;
        watches.CreateHandler = (command, _) => first ? FailFirst() : Task.FromResult(SearchWatches.Snapshot(command));
        Task<SnuWatcher.Application.Persistence.SubscriptionSnapshot> FailFirst() { first = false; throw new IOException("lost return"); }
        await using var view = new WatchSelectionViewModel(watches, dispatcher, new SearchClock());
        await dispatcher.InvokeAsync(() => view.ReplaceSelection([SearchQueries.Row(Key())]));
        await view.SaveCommand.ExecuteAsync();
        Assert.Equal(WatchSelectionStatus.RetryRequired, view.Model.State.Status);
        await dispatcher.InvokeAsync(() => view.Label = "Changed while ambiguous");
        await view.SaveCommand.ExecuteAsync();
        Assert.Equal(2, watches.Creates.Count);
        Assert.Same(watches.Creates[0], watches.Creates[1]);
        Assert.NotEqual("Changed while ambiguous", watches.Creates[1].Label);
        Assert.Equal(WatchSelectionStatus.Saved, view.Model.State.Status);
    }

    /// <summary>Cancellation cannot discard an admitted mutation or its returned receipt.</summary>
    [Fact]
    public async Task CancelAndDisposeCannotAbandonAnAdmittedCreateOrDropItsActualReceipt()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var watches = new SearchWatches();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken admitted = default;
        watches.CreateHandler = async (command, token) => { admitted = token; started.SetResult(); await release.Task; return SearchWatches.Snapshot(command); };
        var view = new WatchSelectionViewModel(watches, dispatcher, new SearchClock());
        await dispatcher.InvokeAsync(() => view.ReplaceSelection([SearchQueries.Row(Key())]));
        var save = view.SaveCommand.ExecuteAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            view.SaveCommand.Cancel();
            var stopping = view.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            Assert.False(admitted.CanBeCanceled);
            release.SetResult();
            await stopping; await save;
            Assert.Equal(WatchSelectionStatus.Saved, view.Model.State.Status);
            Assert.NotNull(view.Model.State.Created);
            Assert.Single(watches.Creates);
        }
        finally { release.TrySetResult(); await save; await view.DisposeAsync(); }
    }

    /// <summary>Mixed terms and already-ended dates never reach the runtime mutation port.</summary>
    [Fact]
    public async Task MixedTermSelectionAndPastExpiryCannotCreateAWatch()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var watches = new SearchWatches();
        await using var view = new WatchSelectionViewModel(watches, dispatcher, new SearchClock());
        await dispatcher.InvokeAsync(() => Assert.Throws<ArgumentException>(() => view.ReplaceSelection([
            SearchQueries.Row(Key()), SearchQueries.Row(new(2026, "U000200002", "OTHER", "F33.107", "002", "0"))])));
        await dispatcher.InvokeAsync(() => { view.ReplaceSelection([SearchQueries.Row(Key())]); view.UseExpiry = true; view.ExpiryDate = new DateTime(2026, 10, 31); });
        await view.SaveCommand.ExecuteAsync();
        Assert.Empty(watches.Creates);
        Assert.Equal(WatchSelectionStatus.Invalid, view.Model.State.Status);
    }

    private static SectionKey Key(string course = "F32.102", string section = "012") => new(2026, "U000200002", "U000300002", course, section, "0");
}
