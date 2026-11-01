using SnuWatcher.Application.Sources;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Tests.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Search;

/// <summary>One latest-selection metadata runner with original identities and timestamps.</summary>
public sealed class CourseDetailsViewModelTests
{
    /// <summary>Latest selection wins even when an obsolete source request ignores cancellation.</summary>
    [Fact]
    public async Task ANewExactSelectionCannotDisplayLateOldDetailsAndDisposeOwnsTheActualRunner()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var queries = new SearchQueries();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SourceRead<CourseDetails>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new SnuWatcher.Core.SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "0");
        var second = new SnuWatcher.Core.SectionKey(2026, "U000200002", "U000300002", "F33.107", "002", "0");
        var ordinary = new SearchQueries();
        queries.DetailsHandler = (key, token) => { if (key == first) { firstStarted.TrySetResult(); return release.Task; } return ordinary.GetDetailsAsync(key, token); };
        await using var view = new CourseDetailsViewModel(queries, dispatcher);
        var firstTask = view.LoadAsync(first);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondTask = view.LoadAsync(second);
            release.SetResult(await ordinary.GetDetailsAsync(first));
            await Task.WhenAll(firstTask, secondTask);
            Assert.Equal(second, view.Model.State.Key);
            Assert.Equal(second, view.Model.State.Read!.Data!.Key);
            Assert.Equal(CourseUiLoadState.Complete, view.Model.State.Status);
            Assert.Equal(new[] { first, second }, queries.Details);
        }
        finally { release.TrySetResult(await ordinary.GetDetailsAsync(first)); await firstTask; }
    }
}
