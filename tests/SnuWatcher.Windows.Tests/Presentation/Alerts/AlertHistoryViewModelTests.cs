using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Alerts;

public sealed class AlertHistoryViewModelTests
{
    [Fact]
    public async Task LoadReadsOnlyOneGlobalPageAndPreservesOriginalOpeningTimeAndSeats()
    {
        await using var f = new AlertFixture();
        Assert.Equal(UiCommandOutcome.Completed, await f.Model.LoadCommand.ExecuteAsync());
        var request = Assert.Single(f.Reader.Pages);
        Assert.Null(request.Cursor);
        Assert.Equal(25, request.Size);
        var row = Assert.Single(f.Model.State.Rows);
        Assert.Equal(4, row.Source.Trigger.CurrentSeats);
        Assert.Equal(AlertFixture.Now.AddMinutes(-5), row.Source.Trigger.ObservedAtUtc);
        Assert.Equal("F32.102 012", row.Identifier);
        Assert.Equal(AlertFixture.Now, f.Model.State.LastSuccessfulReadAtUtc);
    }

    [Fact]
    public async Task FailedReloadKeepsOldRowsAndLastSuccessfulReadButMarksThemStale()
    {
        await using var f = new AlertFixture();
        await f.Model.LoadCommand.ExecuteAsync();
        var before = f.Model.State.Rows;
        f.Reader.Page = (_, _) => throw new IOException("private database detail");
        await f.Model.ReloadCommand.ExecuteAsync();
        Assert.Equal(before, f.Model.State.Rows);
        Assert.True(f.Model.State.IsStale);
        Assert.Equal("Ui.AlertHistoryUnavailable", f.Model.State.MessageKey);
        Assert.Equal(AlertFixture.Now, f.Model.State.LastSuccessfulReadAtUtc);
    }
}
