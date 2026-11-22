using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Watches;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Watches;

public sealed class WatchListEdgeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource<T> Barrier<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(CheckOutcome.Unavailable, "Ui.Unknown")]
    [InlineData(CheckOutcome.RateLimited, "Ui.SourceRateLimited")]
    [InlineData(CheckOutcome.AccessRestricted, "Ui.SourceRestricted")]
    [InlineData(CheckOutcome.NotFound, "Ui.SectionNotFound")]
    [InlineData(CheckOutcome.Incomplete, "Ui.PartialResults")]
    public async Task FailedOutcomesKeepTheSameHistoricalPairAndLocalizedFailureKey(CheckOutcome outcome, string key)
    {
        await using var fixture = new WatchPresentationFixture();
        var good = fixture.Current(WatchPresentationFixture.Key(), 116, 120, TimeSpan.FromSeconds(10));
        fixture.Add(1, sections: [new(good.Key, 2, 2, outcome, fixture.Clock.GetUtcNow(), good.LastSuccessfulAtUtc, good.Baseline)]);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        var member = Assert.Single(fixture.ViewModel.State.Details!.Members);
        Assert.Equal(key, member.OutcomeKey);
        Assert.Equal("116/120", member.CountsText);
        Assert.Equal("—", member.CurrentSeatsText);
        Assert.Equal("Ui.Unknown", member.DataStateKey);
        Assert.Equal(good.LastSuccessfulAtUtc, member.Health.LastSuccessfulAtUtc);
    }

    [Fact]
    public async Task UncheckedAndFutureDatedCountsNeverBecomeAFullOrFreshBadge()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1, sections: [SectionSnapshot.Unchecked(WatchPresentationFixture.Key())]);
        fixture.Add(2, sections: [fixture.Current(WatchPresentationFixture.Key("002"), 40, 40, TimeSpan.FromSeconds(-1))]);
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        var uncheckedRow = fixture.ViewModel.State.Rows[0];
        Assert.Equal("Ui.NotChecked", uncheckedRow.DataStateKey);
        Assert.Equal("—", uncheckedRow.CountsText);
        Assert.Equal("—", uncheckedRow.LastSuccessText);
        Assert.Equal("Ui.Unknown", uncheckedRow.AvailabilityKey);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        var member = fixture.ViewModel.State.Details!.Members[0];
        Assert.Equal("Ui.NotChecked", member.OutcomeKey);
        Assert.Equal("—", member.AgeText);
        Assert.Equal("F32.102 012", member.Identifier);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(2));
        var future = fixture.ViewModel.State.Details!.Members[0];
        Assert.Equal("Ui.ClockChanged", future.DataStateKey);
        Assert.Equal("Ui.Unknown", future.AvailabilityKey);
        Assert.Equal("—", future.AgeText);
        Assert.Equal("Ui.CheckSuccess", future.OutcomeKey);
    }

    [Theory]
    [InlineData(WatchStatus.Active, "Ui.Active")]
    [InlineData(WatchStatus.Paused, "Ui.Paused")]
    [InlineData(WatchStatus.Expired, "Ui.Expired")]
    public async Task LifecycleRemainsSeparateFromCurrentOpenData(WatchStatus status, string key)
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1, status: status);
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        var row = Assert.Single(fixture.ViewModel.State.Rows);
        Assert.Equal(key, row.StatusKey);
        Assert.Equal("Ui.Open", row.AvailabilityKey);
        Assert.Equal(1, row.MemberCount);
        Assert.Equal(2026, row.Term.Year);
        Assert.Equal("2학기", row.TermLabel);
        await fixture.ViewModel.SelectWatchAsync(row.Id);
        Assert.Equal(key, fixture.ViewModel.State.Details!.StatusKey);
    }

    [Fact]
    public async Task ExpiredWatchNeedsAnExtendedExpiryBeforeResume()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1, status: WatchStatus.Expired, expiry: WatchExpiry.FromLocalDate(new(2026, 10, 31)));
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        Assert.Equal(WatchStatus.Expired, fixture.ViewModel.State.Rows[0].Status);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        Assert.False(fixture.ViewModel.ResumeCommand.CanExecute(null));
        await fixture.Dispatcher.InvokeAsync(() => { fixture.ViewModel.NoExpiry = false; fixture.ViewModel.ExpiryDate = new(2026, 11, 4); });
        await fixture.ViewModel.SaveExpiryCommand.ExecuteAsync();
        Assert.True(fixture.ViewModel.ResumeCommand.CanExecute(null));
        await fixture.ViewModel.ResumeCommand.ExecuteAsync();
        Assert.IsType<ResumeSubscription>(fixture.Changes.Last());
        Assert.Equal(WatchStatus.Active, fixture.ViewModel.State.Details!.Health.EffectiveWatchStatus);
    }

    [Fact]
    public async Task BoundSelectionStartsOwnedDetailsAndIgnoresTransientNullOrOldPageRows()
    {
        await using var fixture = new WatchPresentationFixture { RowsPerPage = 1 };
        fixture.Add(1); fixture.Add(2);
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        var stale = fixture.ViewModel.State.Rows[0];
        await fixture.ViewModel.NextCommand.ExecuteAsync();
        var selected = Barrier<bool>();
        fixture.ViewModel.PropertyChanged += (_, _) =>
        {
            if (fixture.ViewModel.State.Details?.Source.Subscription.Id == WatchPresentationFixture.Id(2)) selected.TrySetResult(true);
        };
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.SelectedRow = fixture.ViewModel.State.Rows[0]);
        await selected.Task.WaitAsync(Deadline);
        Assert.Equal(WatchPresentationFixture.Id(2), fixture.ViewModel.SelectedRow!.Id);
        await fixture.Dispatcher.InvokeAsync(() => { fixture.ViewModel.SelectedRow = null; fixture.ViewModel.SelectedRow = stale; fixture.ViewModel.SelectedMember = null; });
        Assert.Equal(WatchPresentationFixture.Id(2), fixture.ViewModel.State.SelectedId);
        Assert.NotNull(fixture.ViewModel.SelectedMember);
    }

    [Fact]
    public async Task BindingReapplyingSameIdentityAfterAgeProjectionDoesNotReadDetailsAgain()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        var before = fixture.DetailReads;
        await fixture.Dispatcher.InvokeAsync(() =>
        {
            fixture.ViewModel.RefreshAges();
            fixture.ViewModel.SelectedRow = fixture.ViewModel.State.Rows[0];
            fixture.ViewModel.SelectedMember = fixture.ViewModel.State.Details!.Members[0];
        });
        // Drain owned selection work, without asking for another reload.
        await fixture.ViewModel.DisposeAsync();
        Assert.Equal(before, fixture.DetailReads);
        Assert.False(fixture.ViewModel.State.SelectionPending);
    }

    [Fact]
    public async Task InvalidExpiryAndWrongThreadEditsCannotReachMutationPort()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        Assert.Throws<InvalidOperationException>(() => fixture.ViewModel.RenameText = "wrong thread");
        Assert.Throws<InvalidOperationException>(fixture.ViewModel.RefreshAges);
        await fixture.Dispatcher.InvokeAsync(() => { fixture.ViewModel.NoExpiry = false; fixture.ViewModel.ExpiryDate = null; });
        await fixture.ViewModel.SaveExpiryCommand.ExecuteAsync();
        Assert.Equal("Ui.ExpiryInvalid", fixture.ViewModel.State.MessageKey);
        Assert.Empty(fixture.Changes);
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.ExpiryDate = DateTime.MaxValue.Date);
        await fixture.ViewModel.SaveExpiryCommand.ExecuteAsync();
        Assert.Equal("Ui.ExpiryInvalid", fixture.ViewModel.State.MessageKey);
        Assert.Empty(fixture.Changes);
    }

    [Fact]
    public async Task FailedDetailsDoNotExposeExceptionTextOrKeepSelectionBusy()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        fixture.ReadDetails = (_, _) => throw new IOException("private selected database filename");
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        Assert.False(fixture.ViewModel.State.SelectionPending);
        Assert.Equal("Ui.CommandFailed", fixture.ViewModel.State.MessageKey);
        Assert.False(fixture.ViewModel.PauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnexpectedMutationFailureIsFixedAndNeverOptimisticallyChangesDefinition()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        fixture.Change = (_, _) => throw new IOException("private mutation path");
        await fixture.ViewModel.PauseCommand.ExecuteAsync();
        Assert.Equal("Ui.CommandFailed", fixture.ViewModel.State.MessageKey);
        Assert.Equal(WatchStatus.Active, fixture.ViewModel.State.Details!.Source.Subscription.Status);
    }

    [Fact]
    public async Task DisposalWaitsForAnUncooperativeActualDetailsReadAndClosesAdmission()
    {
        await using var fixture = new WatchPresentationFixture();
        var details = fixture.Add(1);
        var entered = Barrier<bool>(); var release = Barrier<WatchDetailsSnapshot?>();
        fixture.ReadDetails = async (_, _) => { entered.TrySetResult(true); return await release.Task; };
        var selecting = fixture.ViewModel.SelectWatchAsync(details.Subscription.Id);
        Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            disposing = fixture.ViewModel.DisposeAsync().AsTask();
            await fixture.Dispatcher.InvokeAsync(() => { });
            Assert.False(disposing.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selecting);
        }
        finally { release.TrySetResult(details); }
        await disposing!.WaitAsync(Deadline);
        Assert.Equal(UiCommandOutcome.Unavailable, await fixture.ViewModel.ReloadCommand.ExecuteAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ViewModel.SelectWatchAsync(details.Subscription.Id));
        await fixture.Dispatcher.InvokeAsync(fixture.ViewModel.RefreshAges);
    }

    [Fact]
    public async Task CancellationRetainsActualPageReadUntilItEnds()
    {
        await using var fixture = new WatchPresentationFixture();
        var entered = Barrier<bool>(); var release = Barrier<WatchPageReadResult>();
        fixture.ReadPage = async (_, _) => { entered.TrySetResult(true); return await release.Task; };
        var reading = fixture.ViewModel.ReloadCommand.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            fixture.ViewModel.ReloadCommand.Cancel();
            Assert.True(fixture.ViewModel.ReloadCommand.IsExecuting);
            Assert.False(reading.IsCompleted);
        }
        finally { release.TrySetResult(new(WatchPageReadDisposition.Page, 1, new(new(1, 0, fixture.Clock.GetUtcNow()), [], false, null))); }
        await reading.WaitAsync(Deadline);
    }

    [Fact]
    public async Task SupersededPageReloadSelectionDoesNotPublishAnActionFailure()
    {
        await using var fixture = new WatchPresentationFixture();
        var first = fixture.Add(1); var second = fixture.Add(2);
        await fixture.ViewModel.SelectWatchAsync(first.Subscription.Id);
        var entered = Barrier<bool>(); var release = Barrier<WatchDetailsSnapshot?>();
        fixture.ReadDetails = async (id, _) =>
        {
            if (id != first.Subscription.Id) return second;
            entered.TrySetResult(true); return await release.Task;
        };
        var seen = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, _) => seen.Add(fixture.ViewModel.State.MessageKey);
        var reload = fixture.ViewModel.ReloadCommand.ExecuteAsync();
        Task? selected = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            selected = fixture.ViewModel.SelectWatchAsync(second.Subscription.Id);
            await fixture.Dispatcher.InvokeAsync(() => { });
        }
        finally { release.TrySetResult(first); }
        await selected!.WaitAsync(Deadline); await reload.WaitAsync(Deadline);
        Assert.DoesNotContain("Ui.CommandFailed", seen);
        Assert.Equal(second.Subscription.Id, fixture.ViewModel.State.SelectedId);
    }

    [Fact]
    public async Task InvalidDependenciesAndEmptyIdentityFailBeforeRead()
    {
        await using var fixture = new WatchPresentationFixture();
        Assert.Throws<ArgumentNullException>(() => new WatchListViewModel(null!, fixture, fixture.Dispatcher, fixture.Clock, () => TimeSpan.FromSeconds(60)));
        Assert.Throws<ArgumentNullException>(() => new WatchListViewModel(fixture, null!, fixture.Dispatcher, fixture.Clock, () => TimeSpan.FromSeconds(60)));
        Assert.Throws<ArgumentNullException>(() => new WatchListViewModel(fixture, fixture, null!, fixture.Clock, () => TimeSpan.FromSeconds(60)));
        Assert.Throws<ArgumentNullException>(() => new WatchListViewModel(fixture, fixture, fixture.Dispatcher, null!, () => TimeSpan.FromSeconds(60)));
        Assert.Throws<ArgumentNullException>(() => new WatchListViewModel(fixture, fixture, fixture.Dispatcher, fixture.Clock, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.ViewModel.SelectWatchAsync(Guid.Empty));
        Assert.Equal(0, fixture.DetailReads);
        Assert.False(fixture.ViewModel.PauseCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.PreviousCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.NextCommand.CanExecute(null));
    }
}
