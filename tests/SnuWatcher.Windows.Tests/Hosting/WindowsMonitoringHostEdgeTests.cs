using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Windows.Hosting;
using Xunit;

namespace SnuWatcher.Windows.Tests.Hosting;

public sealed class WindowsMonitoringHostEdgeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);
    private static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "012", "0");

    [Fact]
    public async Task BlockedSourceMustActuallyFinishBeforeStopClosesResourcesOrReleasesOwner()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new EdgeSource(blockSearch: true);
        var channel = new EdgeChannel();
        var (host, store) = await OpenAsync(fixture.Profile, source, channel);
        try
        {
            await AddWatchAsync(host);
            await source.SearchStarted.Task.WaitAsync(Timeout);
            var stopping = host.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.Equal(WindowsMonitoringHostState.Stopping, host.State);
            Assert.False(source.Disposed);
            Assert.Single(await store.ListSubscriptionsAsync(default));
            await AssertOwnerHeldAsync(fixture.Profile);
            source.ReleaseSearch.TrySetResult();
            Assert.True((await stopping.WaitAsync(Timeout)).IsFinalized);
            Assert.True(source.Disposed);
            Assert.Equal(0, channel.Calls);
            await AssertOwnerAvailableAsync(fixture.Profile);
        }
        finally { source.ReleaseSearch.TrySetResult(); await CloseAsync(host); }
    }

    [Fact]
    public async Task BlockedActualChannelRetainsOwnerAndAuthoritativeAcceptanceIsWrittenBeforeFinalStop()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new EdgeSource();
        var channel = new EdgeChannel(block: true);
        var (host, store) = await OpenAsync(fixture.Profile, source, channel);
        try
        {
            await AddWatchAsync(host);
            await channel.Started.Task.WaitAsync(Timeout);
            var pending = Assert.Single(await store.ReadPendingDeliveriesAsync(20, default));
            var stopping = host.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.False(source.Disposed);
            await AssertOwnerHeldAsync(fixture.Profile);
            channel.Release.TrySetResult(new BatchChannelResult(BatchChannelOutcome.OsAccepted));
            Assert.True((await stopping.WaitAsync(Timeout)).IsFinalized);
            Assert.True(source.Disposed);
            Assert.Equal(1, channel.Calls);
            await AssertOwnerAvailableAsync(fixture.Profile);
            await using var verify = await SqliteWatchStore.OpenAsync(new(fixture.Profile.DatabasePath));
            var accepted = (await verify.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
            Assert.Equal(DeliveryStatus.OsAccepted, accepted.Status);
            Assert.Equal(1, accepted.Attempts);
        }
        finally { channel.Release.TrySetResult(new BatchChannelResult(BatchChannelOutcome.OsAccepted)); await CloseAsync(host); }
    }

    [Fact]
    public async Task AcceptedChannelWithPersistentResultFaultBlocksStopAndRetryFinalizesWithoutRestartOrResend()
    {
        using var fixture = new RuntimeProfileFixture();
        var fail = 1;
        var source = new EdgeSource();
        var channel = new EdgeChannel();
        var (host, store) = await OpenAsync(fixture.Profile, source, channel, stage =>
        { if (stage == SqliteStoreWriteStage.BatchResult && Volatile.Read(ref fail) != 0) throw new IOException("Private injected result write failure."); });
        try
        {
            await AddWatchAsync(host);
            await channel.Started.Task.WaitAsync(Timeout);
            var stopped = await host.StopAsync().WaitAsync(Timeout);
            Assert.False(stopped.IsFinalized);
            Assert.Equal(WindowsMonitoringHostState.StopBlocked, stopped.State);
            Assert.Equal(WindowsMonitoringHostState.StopBlocked, host.State);
            Assert.Single(stopped.LogicalResult!.PendingKnownSections);
            Assert.False(source.Disposed);
            await AssertOwnerHeldAsync(fixture.Profile);
            var pending = Assert.Single(await store.ReadPendingDeliveriesAsync(20, default));
            Assert.Equal(0, pending.Attempts);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask());
            await AssertOwnerHeldAsync(fixture.Profile);
            var sourceCalls = source.Calls;
            Volatile.Write(ref fail, 0);
            var finalized = await host.RetryFinalizeStopAsync().WaitAsync(Timeout);
            Assert.True(finalized.IsFinalized);
            Assert.Equal(sourceCalls, source.Calls);
            Assert.Equal(1, channel.Calls);
            Assert.True(source.Disposed);
            await AssertOwnerAvailableAsync(fixture.Profile);
            await using var verify = await SqliteWatchStore.OpenAsync(new(fixture.Profile.DatabasePath));
            var accepted = (await verify.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
            Assert.Equal(DeliveryStatus.OsAccepted, accepted.Status);
            Assert.Equal(1, accepted.Attempts);
        }
        finally { Volatile.Write(ref fail, 0); await CloseAsync(host); }
    }

    [Fact]
    public async Task CancelingResumeWaiterDoesNotCancelActualControlAndStopWaitsForIt()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new EdgeSource();
        var entered = Signal(); var release = Signal();
        CancellationToken actualToken = default;
        var (host, _) = await OpenAsync(fixture.Profile, source, new EdgeChannel(), resume: async (_, token) =>
        {
            actualToken = token;
            entered.TrySetResult();
            await release.Task;
            return Resumed();
        });
        try
        {
            using var cancellation = new CancellationTokenSource();
            var caller = host.ResumeSourceAccessAsync(0, cancellation.Token);
            await entered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
            Assert.False(actualToken.IsCancellationRequested);
            var stopping = host.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.False(source.Disposed);
            await AssertOwnerHeldAsync(fixture.Profile);
            release.TrySetResult();
            Assert.True((await stopping.WaitAsync(Timeout)).IsFinalized);
            Assert.True(source.Disposed);
            await AssertOwnerAvailableAsync(fixture.Profile);
        }
        finally { release.TrySetResult(); await CloseAsync(host); }
    }

    [Fact]
    public async Task QueuedRecoveryObservesClosingAndNeverInvokesItsUnderlyingControl()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new EdgeSource();
        var entered = Signal(); var release = Signal(); var recoverCalls = 0;
        var (host, _) = await OpenAsync(fixture.Profile, source, new EdgeChannel(), recover: _ =>
        { Interlocked.Increment(ref recoverCalls); return Task.CompletedTask; }, resume: async (_, _) =>
        { entered.TrySetResult(); await release.Task; return Resumed(); });
        try
        {
            var active = host.ResumeSourceAccessAsync(0);
            await entered.Task.WaitAsync(Timeout);
            var queued = host.RecoverSourcePolicyAsync();
            Assert.False(queued.IsCompleted);
            var stopping = host.StopAsync();
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            Assert.True((await active.WaitAsync(Timeout)).Applied);
            var error = await Record.ExceptionAsync(() => queued);
            Assert.True(error is InvalidOperationException or OperationCanceledException);
            Assert.Equal(0, Volatile.Read(ref recoverCalls));
            Assert.True((await stopping.WaitAsync(Timeout)).IsFinalized);
            await AssertOwnerAvailableAsync(fixture.Profile);
        }
        finally { release.TrySetResult(); await CloseAsync(host); }
    }

    [Fact]
    public async Task TamperedProfileIsRejectedBeforeAnyResourceFactoryAndCannotLeakOwner()
    {
        using var fixture = new RuntimeProfileFixture();
        File.WriteAllText(fixture.Profile.ManifestPath, "{\"format\":\"private-invalid-format\",\"runId\":\"" + fixture.Profile.RunId!.Value.ToString("N") + "\"}");
        var stores = 0; var sources = 0;
        var factories = new WindowsMonitoringHostFactories((_, _, _) =>
        { Interlocked.Increment(ref stores); throw new InvalidOperationException("Must reject before opening any store."); }, (_, _, _) =>
        { Interlocked.Increment(ref sources); throw new InvalidOperationException("Must reject before opening any source."); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsMonitoringHost.OpenForTestingAsync(fixture.Profile, new EdgeChannel(), factories));
        Assert.Equal(0, stores);
        Assert.Equal(0, sources);
        Assert.False(File.Exists(fixture.Profile.DatabasePath));
        await AssertOwnerAvailableAsync(fixture.Profile);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SourceAccessResumeResult Resumed() => new(true, SourcePolicySnapshot.Initial(SourcePolicyIds.SnuSugang));
    private static Task<SubscriptionSnapshot> AddWatchAsync(WindowsMonitoringHost host) => host.Session.CreateAsync(
        new(Guid.NewGuid(), "Private host ownership test", SubscriptionKind.Section, [Key], true, null, DateTimeOffset.UtcNow));

    private static async Task<(WindowsMonitoringHost Host, SqliteWatchStore Store)> OpenAsync(RuntimeProfile profile,
        EdgeSource source, EdgeChannel channel, Action<SqliteStoreWriteStage>? fault = null,
        Func<CancellationToken, Task>? recover = null,
        Func<long, CancellationToken, Task<SourceAccessResumeResult>>? resume = null)
    {
        SqliteWatchStore? store = null;
        var factories = new WindowsMonitoringHostFactories(async (selected, clock, token) =>
        {
            store = await SqliteWatchStore.OpenAsync(new(selected.DatabasePath), clock, fault, token);
            return store;
        }, (_, _, _) => Task.FromResult(new WindowsMonitoringHostSource(source,
            () => ValueTask.CompletedTask, source.DisposeAsync, recover: recover, resume: resume)));
        var opened = await WindowsMonitoringHost.OpenForTestingAsync(profile, channel, factories).WaitAsync(Timeout);
        Assert.Equal(WindowsHostOpenDisposition.Started, opened.Disposition);
        return (opened.Host!, store!);
    }

    private static async Task AssertOwnerHeldAsync(RuntimeProfile profile)
    {
        var contender = await DatabaseOwnerGate.TryAcquireAsync(profile.ApplicationId);
        await using var unexpectedLease = contender.Lease;
        Assert.Equal(DatabaseOwnerDisposition.AlreadyOwned, contender.Disposition);
    }

    private static async Task AssertOwnerAvailableAsync(RuntimeProfile profile)
    {
        var next = await DatabaseOwnerGate.TryAcquireAsync(profile.ApplicationId);
        await using var lease = next.Lease;
        Assert.Equal(DatabaseOwnerDisposition.Acquired, next.Disposition);
    }

    private static async Task CloseAsync(WindowsMonitoringHost host)
    {
        var stopped = await host.StopAsync().WaitAsync(Timeout);
        if (!stopped.IsFinalized) await host.RetryFinalizeStopAsync().WaitAsync(Timeout);
        await host.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    private sealed class EdgeSource(bool blockSearch = false) : ICourseSource, IAsyncDisposable
    {
        private readonly HostTestSource _inner = new();
        private int _disposed;
        internal TaskCompletionSource SearchStarted { get; } = Signal();
        internal TaskCompletionSource ReleaseSearch { get; } = Signal();
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        internal int Calls => Volatile.Read(ref _inner.Calls);
        public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken token) =>
            _inner.GetCatalogAsync(query, purpose, token);
        public async Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken token)
        {
            SearchStarted.TrySetResult();
            if (blockSearch) await ReleaseSearch.Task;
            return await _inner.SearchAsync(query, purpose, token);
        }
        public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken token) =>
            _inner.GetDetailsAsync(key, purpose, token);
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return _inner.DisposeAsync(); }
    }

    private sealed class EdgeChannel : IBatchDesktopAlertChannel
    {
        private int _calls;
        internal EdgeChannel(bool block = false) { if (!block) Release.SetResult(new BatchChannelResult(BatchChannelOutcome.OsAccepted)); }
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource Started { get; } = Signal();
        internal TaskCompletionSource<BatchChannelResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(BatchChannelReadiness.Ready); }
        public Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken token)
        { Interlocked.Increment(ref _calls); Started.TrySetResult(); return Release.Task; }
    }
}
