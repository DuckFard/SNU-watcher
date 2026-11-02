using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Common;

public sealed class UiSnapshotModelTests
{
    [Fact]
    public async Task ReplacesWholeSnapshotAndPublishesOnlyOnDispatcher()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var original = new ExampleSnapshot("통계학", "012");
        var replacement = new ExampleSnapshot("Statistics", "002");
        var model = new UiSnapshotModel<ExampleSnapshot>(original, dispatcher);
        var calls = new List<(string? Property, int Thread)>();
        model.PropertyChanged += (_, args) => calls.Add((args.PropertyName, Environment.CurrentManagedThreadId));

        await model.ReplaceAsync(replacement);

        Assert.Same(replacement, model.State);
        Assert.Equal("통계학", original.Name);
        var call = Assert.Single(calls);
        Assert.Equal(nameof(model.State), call.Property);
        Assert.Equal(dispatcher.ThreadId, call.Thread);
        await model.ReplaceAsync(replacement);
        Assert.Single(calls);
    }

    [Fact]
    public async Task PublicationAlreadyOnDispatcherDoesNotRequireAnotherQueueTurn()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var model = new UiSnapshotModel<ExampleSnapshot>(new("One", "001"), dispatcher);
        var replacement = new ExampleSnapshot("Two", "002");
        await dispatcher.InvokeAsync(() => Assert.True(model.ReplaceAsync(replacement).IsCompletedSuccessfully));
        Assert.Same(replacement, model.State);
    }

    [Fact]
    public async Task PreCanceledReplacementLeavesOriginalStateAndSendsNoEvent()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var original = new ExampleSnapshot("One", "001");
        var model = new UiSnapshotModel<ExampleSnapshot>(original, dispatcher);
        var events = 0;
        model.PropertyChanged += (_, _) => events++;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.ReplaceAsync(new("Two", "002"), canceled.Token));

        Assert.Same(original, model.State);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task OneBadObserverDoesNotHidePublishedStateFromOtherObservers()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var errors = new List<string>();
        var model = new UiSnapshotModel<ExampleSnapshot>(new("One", "001"), dispatcher, errors.Add);
        model.PropertyChanged += (_, _) => throw new IOException("private observer detail");
        var observed = false;
        model.PropertyChanged += (_, _) => observed = true;
        var replacement = new ExampleSnapshot("Two", "002");

        await model.ReplaceAsync(replacement);

        Assert.True(observed);
        Assert.Same(replacement, model.State);
        Assert.Equal("ui_state_notification_failed", Assert.Single(errors));
    }

    [Fact]
    public async Task InvalidInitialOrReplacementValuesAreRejectedBeforePublication()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        Assert.Throws<ArgumentNullException>(() => new UiSnapshotModel<ExampleSnapshot>(null!, dispatcher));
        Assert.Throws<ArgumentNullException>(() => new UiSnapshotModel<ExampleSnapshot>(new("One", "001"), null!));
        var model = new UiSnapshotModel<ExampleSnapshot>(new("One", "001"), dispatcher);
        await Assert.ThrowsAsync<ArgumentNullException>(() => model.ReplaceAsync(null!));
    }

    [Fact]
    public async Task SynchronousReplacementRejectsWrongThreadWithoutChangingState()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var original = new ExampleSnapshot("One", "001");
        var model = new UiSnapshotModel<ExampleSnapshot>(original, dispatcher);
        var failure = Assert.Throws<InvalidOperationException>(() => model.Replace(new("Two", "002")));
        Assert.Contains("dispatcher", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(original, model.State);
    }

    [Fact]
    public async Task SynchronousReplacementOnDispatcherPublishesBeforeReturningToBoundSetter()
    {
        await using var dispatcher = new DedicatedUiDispatcher();
        var model = new UiSnapshotModel<ExampleSnapshot>(new("One", "001"), dispatcher);
        var replacement = new ExampleSnapshot("통계학", "012");
        var published = false;
        model.PropertyChanged += (_, _) =>
        {
            Assert.Equal(dispatcher.ThreadId, Environment.CurrentManagedThreadId);
            published = ReferenceEquals(model.State, replacement);
        };
        await dispatcher.InvokeAsync(() =>
        {
            model.Replace(replacement);
            Assert.True(published);
            Assert.Same(replacement, model.State);
        });
    }

    private sealed record ExampleSnapshot(string Name, string Section);
}
