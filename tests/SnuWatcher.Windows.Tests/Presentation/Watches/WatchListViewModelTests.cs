using SnuWatcher.Application.Monitoring.Health;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Watches;

public sealed class WatchListViewModelTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource<T> Barrier<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task FirstPageIsBoundedAndPreservesKoreanLabelsAndLeadingZeroSections()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1, "통계학", sections: [fixture.Current(WatchPresentationFixture.Key())]);
        fixture.Add(2, "글쓰기", sections: [fixture.Current(WatchPresentationFixture.Key("001")), SectionSnapshot.Unchecked(WatchPresentationFixture.Key("002"))]);
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.ReloadCommand.ExecuteAsync().WaitAsync(Deadline));
        Assert.Equal(25, Assert.Single(fixture.PageSizes));
        Assert.Equal(2, fixture.ViewModel.State.Rows.Length);
        var section = fixture.ViewModel.State.Rows[0];
        Assert.Equal("통계학", section.Label);
        Assert.Equal("F32.102 012", section.Identifier);
        Assert.Equal("39/40", section.CountsText);
        Assert.Equal("Ui.GroupUnmeasured", fixture.ViewModel.State.Rows[1].AvailabilityKey);
        Assert.Equal(0, fixture.DetailReads);
    }

    [Fact]
    public async Task LocalAgeRefreshUsesAppliedTargetAndNeverReadsTheDatabaseAgain()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1, sections: [fixture.Current(WatchPresentationFixture.Key(), age: TimeSpan.FromMinutes(3))]);
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        var before = fixture.ViewModel.State.Details!.Members[0];
        var pageReads = fixture.PageReads; var detailReads = fixture.DetailReads;
        Assert.Equal("Ui.Delayed", before.DataStateKey);
        Assert.Equal("Ui.Unknown", before.AvailabilityKey);
        fixture.AppliedTarget = TimeSpan.FromSeconds(120);
        await fixture.Dispatcher.InvokeAsync(fixture.ViewModel.RefreshAges);
        var after = fixture.ViewModel.State.Details!.Members[0];
        Assert.Equal("Ui.Current", after.DataStateKey);
        Assert.Equal(before.Health.LastSuccessfulAtUtc, after.Health.LastSuccessfulAtUtc);
        Assert.Equal("39/40", after.CountsText);
        Assert.Equal("1", after.CurrentSeatsText);
        Assert.Equal(pageReads, fixture.PageReads);
        Assert.Equal(detailReads, fixture.DetailReads);
    }

    [Fact]
    public async Task LatestFailureRetainsHistoricalCountsAndSeparateOriginalTimes()
    {
        await using var fixture = new WatchPresentationFixture();
        var good = fixture.Current(WatchPresentationFixture.Key(), 116, 120, TimeSpan.FromSeconds(20));
        var failed = new SectionSnapshot(good.Key, 2, 2, CheckOutcome.ParseChanged, fixture.Clock.GetUtcNow(),
            good.LastSuccessfulAtUtc, good.Baseline);
        fixture.Add(1, sections: [failed]);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        var member = Assert.Single(fixture.ViewModel.State.Details!.Members);
        Assert.Equal("116/120", member.CountsText);
        Assert.Equal("—", member.CurrentSeatsText);
        Assert.Equal("Ui.Unknown", member.AvailabilityKey);
        Assert.Equal("Ui.SourceChanged", member.OutcomeKey);
        Assert.NotEqual(member.LastCheckText, member.LastSuccessText);
        Assert.Contains("KST", member.LastSuccessText);
    }

    [Theory]
    [InlineData(39, false, "Ui.Open", "1/2")]
    [InlineData(40, false, "Ui.Unknown", "1/2")]
    [InlineData(40, true, "Ui.Full", "2/2")]
    public async Task GroupAvailabilityNeverCallsPartialCoverageAllFull(int enrolled, bool secondCurrent, string availability, string coverage)
    {
        await using var fixture = new WatchPresentationFixture();
        var first = fixture.Current(WatchPresentationFixture.Key("001"), enrolled);
        var secondKey = WatchPresentationFixture.Key("002");
        fixture.Add(1, sections: [first, secondCurrent ? fixture.Current(secondKey, 40) : SectionSnapshot.Unchecked(secondKey)]);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        Assert.Equal(availability, fixture.ViewModel.State.Details!.AvailabilityKey);
        Assert.Equal(coverage, fixture.ViewModel.State.Details.CoverageText);
    }

    [Fact]
    public async Task DirectSelectionCanLoadANewWatchOutsideCurrentPage()
    {
        await using var fixture = new WatchPresentationFixture { RowsPerPage = 1 };
        fixture.Add(1); fixture.Add(9, "새 관심 강좌");
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(9));
        Assert.Null(fixture.ViewModel.SelectedRow);
        Assert.Equal(WatchPresentationFixture.Id(9), fixture.ViewModel.State.SelectedId);
        Assert.Equal("새 관심 강좌", fixture.ViewModel.State.Details!.Label);
    }

    [Fact]
    public async Task FirstDirectSelectionAlsoLoadsOneInitialListPageWithoutDuplicateDetails()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1, "Newly saved watch");
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        Assert.True(fixture.ViewModel.State.IsLoaded);
        Assert.Equal("Newly saved watch", Assert.Single(fixture.ViewModel.State.Rows).Label);
        Assert.Equal(1, fixture.PageReads);
        Assert.Equal(1, fixture.DetailReads);
    }

    [Fact]
    public async Task EpochRestartClearsHistoryButPreservesStableDirectSelection()
    {
        await using var fixture = new WatchPresentationFixture { RowsPerPage = 1 };
        fixture.Add(1); fixture.Add(2); fixture.Add(9);
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(9));
        fixture.Epoch++;
        await fixture.ViewModel.NextCommand.ExecuteAsync();
        Assert.Equal(1, fixture.ViewModel.State.PageNumber);
        Assert.False(fixture.ViewModel.State.HasPrevious);
        Assert.Equal("Ui.PageRestarted", fixture.ViewModel.State.PagingNoticeKey);
        Assert.Equal(WatchPresentationFixture.Id(9), fixture.ViewModel.State.Details!.Source.Subscription.Id);
    }

    [Fact]
    public async Task BoundedBackwardHistoryDoesNotCutOffForwardReachability()
    {
        await using var fixture = new WatchPresentationFixture { RowsPerPage = 1 };
        for (var identity = 1; identity <= 72; identity++) fixture.Add(identity);
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        for (var index = 0; index < 70; index++) await fixture.ViewModel.NextCommand.ExecuteAsync();
        Assert.Equal(71, fixture.ViewModel.State.PageNumber);
        Assert.Equal(WatchPresentationFixture.Id(71), Assert.Single(fixture.ViewModel.State.Rows).Id);
        Assert.True(fixture.ViewModel.State.BackHistoryLimited);
        for (var index = 0; index < 64; index++) await fixture.ViewModel.PreviousCommand.ExecuteAsync();
        Assert.Equal(7, fixture.ViewModel.State.PageNumber);
        Assert.False(fixture.ViewModel.State.HasPrevious);
        await fixture.ViewModel.FirstCommand.ExecuteAsync();
        Assert.Equal(1, fixture.ViewModel.State.PageNumber);
        Assert.False(fixture.ViewModel.State.BackHistoryLimited);
        Assert.All(fixture.PageSizes, size => Assert.Equal(25, size));
    }

    [Fact]
    public async Task LateUncooperativeDetailsNeverOverwriteTheLatestSelection()
    {
        await using var fixture = new WatchPresentationFixture();
        var first = fixture.Add(1); var second = fixture.Add(2, "두 번째");
        var entered = Barrier<bool>(); var release = Barrier<WatchDetailsSnapshot?>();
        fixture.ReadDetails = (id, _) => id == first.Subscription.Id ? Hold() : Task.FromResult<WatchDetailsSnapshot?>(second);
        async Task<WatchDetailsSnapshot?> Hold() { entered.TrySetResult(true); return await release.Task; }
        var firstSelection = fixture.ViewModel.SelectWatchAsync(first.Subscription.Id);
        Task? secondSelection = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            secondSelection = fixture.ViewModel.SelectWatchAsync(second.Subscription.Id);
            await fixture.Dispatcher.InvokeAsync(() => { });
            Assert.Null(fixture.ViewModel.State.Details);
        }
        finally { release.TrySetResult(first); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstSelection);
        await secondSelection!.WaitAsync(Deadline);
        Assert.Equal("두 번째", fixture.ViewModel.State.Details!.Label);
        Assert.Equal(2, fixture.DetailReads);
    }

    [Fact]
    public async Task MutationUsesExactRevisionAndDoesNotOptimisticallyPauseBeforeReceipt()
    {
        await using var fixture = new WatchPresentationFixture();
        var before = fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(before.Subscription.Id);
        var entered = Barrier<SubscriptionChange>(); var release = Barrier<bool>();
        fixture.Change = async (change, _) => { entered.TrySetResult(change); await release.Task; return fixture.Apply(change); };
        var pausing = fixture.ViewModel.PauseCommand.ExecuteAsync();
        try
        {
            var command = Assert.IsType<PauseSubscription>(await entered.Task.WaitAsync(Deadline));
            Assert.Equal(before.Subscription.DefinitionRevision, command.ExpectedRevision);
            Assert.Equal(fixture.Clock.GetUtcNow(), command.ChangedAtUtc);
            Assert.Equal(WatchStatus.Active, fixture.ViewModel.State.Details!.Source.Subscription.Status);
            Assert.True(fixture.ViewModel.State.IsMutating);
        }
        finally { release.TrySetResult(true); }
        await pausing.WaitAsync(Deadline);
        Assert.Equal(WatchStatus.Paused, fixture.ViewModel.State.Details!.Source.Subscription.Status);
        Assert.Equal("Ui.WatchSaved", fixture.ViewModel.State.MessageKey);
    }

    [Fact]
    public async Task RenameValidatesAndPreservesOriginalUntilConfirmedReload()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.RenameText = new string('a', 121));
        await fixture.ViewModel.RenameCommand.ExecuteAsync();
        Assert.Equal("Ui.WatchLabelInvalid", fixture.ViewModel.State.MessageKey);
        Assert.Empty(fixture.Changes);
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.RenameText = "  통계학 어느 강좌든  ");
        await fixture.ViewModel.RenameCommand.ExecuteAsync();
        Assert.Equal("통계학 어느 강좌든", Assert.IsType<RenameSubscription>(Assert.Single(fixture.Changes)).Label);
        Assert.Equal("통계학 어느 강좌든", fixture.ViewModel.State.Details!.Label);
    }

    [Fact]
    public async Task ExpiryUsesInclusiveKoreaCalendarAndCanBeRemoved()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        await fixture.Dispatcher.InvokeAsync(() => { fixture.ViewModel.NoExpiry = false; fixture.ViewModel.ExpiryDate = new(2026, 11, 3); });
        await fixture.ViewModel.SaveExpiryCommand.ExecuteAsync();
        var expiry = Assert.IsType<SetSubscriptionExpiry>(fixture.Changes.First()).Expiry!;
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero), expiry.ExclusiveUtc);
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.NoExpiry = true);
        await fixture.ViewModel.SaveExpiryCommand.ExecuteAsync();
        Assert.Null(Assert.IsType<SetSubscriptionExpiry>(fixture.Changes.Last()).Expiry);
    }

    [Fact]
    public async Task CasConflictReloadsActualStateWithoutPretendingMutationApplied()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        fixture.Change = (change, _) =>
        {
            fixture.Apply(new RenameSubscription(change.Id, change.ExpectedRevision, fixture.Clock.GetUtcNow(), "Changed elsewhere"));
            throw new StoreVersionConflictException();
        };
        await fixture.ViewModel.PauseCommand.ExecuteAsync();
        Assert.Equal("Ui.WatchConflict", fixture.ViewModel.State.MessageKey);
        Assert.Equal("Changed elsewhere", fixture.ViewModel.State.Details!.Label);
        Assert.Equal(WatchStatus.Active, fixture.ViewModel.State.Details.Source.Subscription.Status);
    }

    [Fact]
    public async Task KnownReceiptSurvivesReloadFailureWithoutAutomaticMutationRetry()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        fixture.Change = (change, _) =>
        {
            var receipt = fixture.Apply(change); fixture.FailReads = true; return Task.FromResult(receipt);
        };
        await fixture.ViewModel.PauseCommand.ExecuteAsync();
        Assert.Equal("Ui.WatchSavedReloadFailed", fixture.ViewModel.State.MessageKey);
        Assert.Single(fixture.Changes);
    }

    [Fact]
    public async Task SuccessfulDetailsFollowupCannotHideFailedPageReloadAfterKnownMutationReceipt()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        fixture.Change = (change, _) =>
        {
            var receipt = fixture.Apply(change);
            fixture.ReadPage = (_, _) => throw new IOException("private page read path");
            return Task.FromResult(receipt);
        };
        await fixture.ViewModel.PauseCommand.ExecuteAsync();
        Assert.Equal(WatchStatus.Paused, fixture.ViewModel.State.Details!.Source.Subscription.Status);
        Assert.Equal(WatchStatus.Active, Assert.Single(fixture.ViewModel.State.Rows).Status);
        Assert.Equal("Ui.WatchSavedReloadFailed", fixture.ViewModel.State.MessageKey);
        Assert.Single(fixture.Changes);
    }

    [Fact]
    public async Task RemoveClearsSelectionOnlyAfterConfirmedPortResult()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        await fixture.ViewModel.RemoveCommand.ExecuteAsync();
        Assert.IsType<RemoveSubscription>(Assert.Single(fixture.Changes));
        Assert.Null(fixture.ViewModel.State.SelectedId);
        Assert.Null(fixture.ViewModel.State.Details);
        Assert.Empty(fixture.ViewModel.State.Rows);
    }

    [Fact]
    public async Task ManualRefreshCallsOnlyTheChosenMemberAndDoesNotBypassRuntimePolicy()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1, sections: [fixture.Current(WatchPresentationFixture.Key("001")), fixture.Current(WatchPresentationFixture.Key("002"))]);
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.SelectedMember = fixture.ViewModel.State.Details!.Members[1]);
        await fixture.ViewModel.RefreshSelectedMemberCommand.ExecuteAsync();
        Assert.Equal("002", Assert.Single(fixture.Refreshes).Section);
        Assert.Equal("Ui.RefreshDeferred", fixture.ViewModel.State.MessageKey);
    }

    [Fact]
    public async Task UnavailableDetailsAndPageErrorsRemainFixedMessages()
    {
        await using var fixture = new WatchPresentationFixture();
        await fixture.ViewModel.SelectWatchAsync(WatchPresentationFixture.Id(9));
        Assert.Null(fixture.ViewModel.State.Details);
        Assert.Equal("Ui.WatchDetailsUnavailable", fixture.ViewModel.State.MessageKey);
        fixture.FailReads = true;
        await fixture.ViewModel.ReloadCommand.ExecuteAsync();
        Assert.Equal("Ui.CommandFailed", fixture.ViewModel.State.MessageKey);
        Assert.DoesNotContain("private", fixture.ViewModel.State.MessageKey!);
    }
}
