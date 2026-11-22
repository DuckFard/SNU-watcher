using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class RuntimeAlertHistoryTests
{
    [Fact]
    public async Task HistoryReadsAreOwnedFacadesWithoutSourceWorkAndCloseWithTheSession()
    {
        var ports = new RuntimePresentationPorts();
        var history = new HistoryPort();
        await using var fixture = new InteractiveRuntimeFixture(presentation: new(ports, ports, ports, history));
        await fixture.Session.StartAsync();
        Assert.NotSame(history, fixture.Session.Alerts);
        Assert.Same(history.EmptyPage, await fixture.Session.Alerts.ReadPageAsync());
        var id = Guid.NewGuid();
        Assert.Null(await fixture.Session.Alerts.ReadDetailsAsync(id));
        Assert.Null(await fixture.Session.Alerts.ReadRecipientsAsync(id));
        Assert.Null(await fixture.Session.Alerts.ReadAttemptsAsync(id));
        Assert.Equal(4, history.Calls);
        Assert.Empty(fixture.Source.Calls);
        Assert.Empty(fixture.Store.Issues);
        await fixture.Session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Alerts.ReadPageAsync());
    }

    [Fact]
    public async Task InvalidHistoryRequestsNeverReachTheStoreOrFaultTheSession()
    {
        var ports = new RuntimePresentationPorts();
        var history = new HistoryPort();
        await using var fixture = new InteractiveRuntimeFixture(presentation: new(ports, ports, ports, history));
        await fixture.Session.StartAsync();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Session.Alerts.ReadPageAsync(pageSize: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Session.Alerts.ReadPageAsync(pageSize: 51));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Session.Alerts.ReadDetailsAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Session.Alerts.ReadRecipientsAsync(id,
            new(Guid.NewGuid(), Guid.NewGuid(), 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Session.Alerts.ReadAttemptsAsync(id,
            new(Guid.NewGuid(), AlertAttemptOrigin.BatchAttempt, 1)));
        Assert.Equal(0, history.Calls);
        Assert.Null(fixture.Session.Failure);
    }

    [Fact]
    public async Task CancelledHistoryWaiterDoesNotAbandonTheActualReadDuringStop()
    {
        var ports = new RuntimePresentationPorts();
        var history = new HistoryPort();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AlertHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        history.ReadPage = _ => { started.TrySetResult(); return release.Task; };
        await using var fixture = new InteractiveRuntimeFixture(presentation: new(ports, ports, ports, history));
        await fixture.Session.StartAsync();
        using var caller = new CancellationTokenSource();
        var read = fixture.Session.Alerts.ReadPageAsync(cancellationToken: caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        var stopping = fixture.Session.StopAsync();
        try { Assert.False(stopping.IsCompleted); }
        finally { release.TrySetResult(history.EmptyPage); }
        Assert.True((await stopping.WaitAsync(TimeSpan.FromSeconds(5))).IsFinalized);
        Assert.Null(fixture.Session.Failure);
    }

    private sealed class HistoryPort : IAlertHistoryReader
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal AlertHistoryPage EmptyPage { get; } = new(new(0, 0, MonitorFixture.Now), 0, [], false, null);
        internal Func<CancellationToken, Task<AlertHistoryPage>>? ReadPage { get; set; }
        public Task<AlertHistoryPage> ReadPageAsync(AlertHistoryCursor? after = null, int pageSize = 25, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref calls); return ReadPage?.Invoke(cancellationToken) ?? Task.FromResult(EmptyPage); }
        public Task<AlertHistoryItem?> ReadDetailsAsync(Guid eventId, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref calls); return Task.FromResult<AlertHistoryItem?>(null); }
        public Task<AlertRecipientPage?> ReadRecipientsAsync(Guid eventId, AlertRecipientCursor? after = null, int pageSize = 25,
            CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref calls); return Task.FromResult<AlertRecipientPage?>(null); }
        public Task<AlertAttemptPage?> ReadAttemptsAsync(Guid eventId, AlertAttemptCursor? after = null, int pageSize = 25,
            CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref calls); return Task.FromResult<AlertAttemptPage?>(null); }
    }
}
