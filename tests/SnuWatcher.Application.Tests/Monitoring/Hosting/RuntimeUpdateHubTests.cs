using Microsoft.Extensions.Time.Testing;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using System.Threading.Channels;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class RuntimeUpdateHubTests
{
    [Fact]
    public async Task SavedStateFailureIsStickyBeforeAnyUiConsumerOrHubWorkerStarts()
    {
        var hub = new RuntimeUpdateHub(new FakeTimeProvider());
        hub.ReportFailure(RuntimeFailureKind.SavedState, "known_result_not_recorded");
        hub.ReportFailure(RuntimeFailureKind.Actor, "monitor_failed");

        var failure = await hub.FailureTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(failure, hub.Failure);
        Assert.Equal(RuntimeFailureKind.SavedState, failure.Kind);
        Assert.Equal("known_result_not_recorded", failure.Code);
    }

    [Theory]
    [InlineData("source_policy_storage_failed", true)]
    [InlineData("source_recovering", false)]
    [InlineData("source_rate_limited", false)]
    public void OnlyTheFixedLocalPolicyStorageFailureIsFatal(string diagnostic, bool fatal)
    {
        var hub = new RuntimeUpdateHub(new FakeTimeProvider(), () => diagnostic);

        hub.CheckSourceDiagnostic();

        Assert.Equal(fatal, hub.Failure is not null);
        if (fatal) Assert.Equal(RuntimeFailureKind.SavedState, hub.Failure!.Kind);
    }

    [Fact]
    public void DiagnosticReaderExceptionNeverLeaksRawDetails()
    {
        var hub = new RuntimeUpdateHub(new FakeTimeProvider(), () => throw new IOException("private source state"));

        hub.CheckSourceDiagnostic();

        Assert.Equal("source_diagnostic_failed", hub.Failure!.Code);
        Assert.Equal(RuntimeFailureKind.SavedState, hub.Failure.Kind);
    }

    [Fact]
    public async Task LaterEmptySnapshotCannotEraseCommitEvidenceBeforeWakeIsConsumed()
    {
        var samples = await SamplesAsync();
        var hub = new RuntimeUpdateHub(new FakeTimeProvider());
        Assert.True(hub.Writer.TryWrite(samples.Commit));
        Assert.True(hub.Writer.TryWrite(samples.Empty));
        Assert.Equal(1, hub.CommittedGeneration);
        Assert.Same(samples.Empty.Snapshot, hub.LatestSnapshot);
        var awakened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var running = hub.RunAsync(changed => awakened.TrySetResult(changed), stop.Token);

        Assert.True(await awakened.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var hint = await hub.Updates.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(samples.Empty.Snapshot, hint.Snapshot);
        Assert.Equal(1, hint.CommittedGeneration);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FullUiQueueCannotHideStickyFailureOrLaterCommittedGeneration()
    {
        var samples = await SamplesAsync();
        var hub = new RuntimeUpdateHub(new FakeTimeProvider());
        var wakeCount = 0;
        using var stop = new CancellationTokenSource();
        var running = hub.RunAsync(_ => Interlocked.Increment(ref wakeCount), stop.Token);
        hub.Writer.TryWrite(samples.Commit);
        await MonitorFixture.EventuallyAsync(() => Volatile.Read(ref wakeCount) == 1);
        hub.Writer.TryWrite(samples.Commit);
        hub.Writer.TryWrite(samples.Empty);
        await MonitorFixture.EventuallyAsync(() => Volatile.Read(ref wakeCount) >= 2);
        hub.ReportFailure(RuntimeFailureKind.SavedState, "known_result_not_recorded");
        var failure = await hub.FailureTask.WaitAsync(TimeSpan.FromSeconds(5));
        hub.Writer.TryWrite(samples.Empty);
        await MonitorFixture.EventuallyAsync(() => hub.Updates.TryPeek(out var hint) && hint.Failure == failure);

        var latest = Assert.IsType<RuntimeUpdate>(await hub.Updates.ReadAsync());

        Assert.Same(failure, latest.Failure);
        Assert.Equal(2, latest.CommittedGeneration);
        Assert.Same(samples.Empty.Snapshot, latest.Snapshot);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DiagnosticIsCheckedAtStartupAndFanoutEvenWhenUiBufferIsFull()
    {
        var samples = await SamplesAsync();
        var diagnostic = "source_recovering";
        var hub = new RuntimeUpdateHub(new FakeTimeProvider(), () => Volatile.Read(ref diagnostic));
        using var stop = new CancellationTokenSource();
        var running = hub.RunAsync(_ => { }, stop.Token);
        hub.Writer.TryWrite(samples.Empty);
        await MonitorFixture.EventuallyAsync(() => hub.Updates.TryPeek(out _));
        Volatile.Write(ref diagnostic, "source_policy_storage_failed");
        hub.Writer.TryWrite(samples.Empty);

        var failure = await hub.FailureTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("source_policy_storage_failed", failure.Code);
        Assert.Equal(RuntimeFailureKind.SavedState, failure.Kind);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompletionDrainsLastHintAndRejectsLaterMonitorWrites()
    {
        var samples = await SamplesAsync();
        var hub = new RuntimeUpdateHub(new FakeTimeProvider());
        hub.Writer.TryWrite(samples.Commit);
        hub.Complete();

        await hub.RunAsync(_ => { }, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(hub.Writer.TryWrite(samples.Empty));
        Assert.False(await hub.Writer.WaitToWriteAsync());
        Assert.True(hub.Updates.TryRead(out var latest));
        Assert.Same(samples.Commit.Snapshot, latest!.Snapshot);
        Assert.False(hub.Updates.TryRead(out _));
        await hub.Updates.Completion;
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.RunAsync(_ => { }, default));
    }

    [Fact]
    public async Task FaultedDeliveryWakeLatchesSanitizedActorFailureAndFaultsOwnedTask()
    {
        var samples = await SamplesAsync();
        var hub = new RuntimeUpdateHub(new FakeTimeProvider());
        hub.Writer.TryWrite(samples.Commit);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hub.RunAsync(
            _ => throw new IOException("private wake failure"), default));

        Assert.Equal(RuntimeFailureKind.Actor, hub.Failure!.Kind);
        Assert.Equal("delivery_wake_failed", hub.Failure.Code);
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public void FailureReportCannotThrowOnMalformedDiagnosticOrClosedUi()
    {
        var hub = new RuntimeUpdateHub(new FakeTimeProvider());
        hub.Complete();

        hub.ReportFailure((RuntimeFailureKind)99, "raw diagnostic with private details");

        Assert.Equal(RuntimeFailureKind.Actor, hub.Failure!.Kind);
        Assert.Equal("runtime_dependency_failed", hub.Failure.Code);
    }

    private static async Task<(WatchMonitorUpdate Commit, WatchMonitorUpdate Empty)> SamplesAsync()
    {
        var store = new FakeMonitorStore([MonitorFixture.Watch([MonitorFixture.Key()])]);
        var clock = new SplitMonitorClock();
        var source = new FakeMonitorSource(store, clock);
        var updates = Channel.CreateUnbounded<WatchMonitorUpdate>();
        var loop = new WatchMonitorLoop(store, source, new FakeMonitorPolicy(), clock,
            new(WatchMonitorProfile.Faster, jitterPercent: 0), updates.Writer);
        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);
        try
        {
            WatchMonitorUpdate committed;
            do { committed = await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            while (committed.CommittedSections.IsEmpty);
            store.Watches = [];
            loop.Wake();
            WatchMonitorUpdate empty;
            do { empty = await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            while (!empty.CommittedSections.IsEmpty || !empty.Snapshot.Queries.IsEmpty);
            return (committed, empty);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
