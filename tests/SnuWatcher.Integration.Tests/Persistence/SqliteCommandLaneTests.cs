using Microsoft.Data.Sqlite;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class SqliteCommandLaneTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SnuWatcher-lane-" + Guid.NewGuid().ToString("N"));
    private SqliteConnectionFactory Factory => new(Path.Combine(_directory, "watch.sqlite3"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunsWholeDatabaseOperationsInOrderOnOneBackgroundWorker()
    {
        await using var lane = new SqliteCommandLane(Factory, capacity: 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var order = new List<int>();
        var first = lane.ExecuteAsync(connection =>
        {
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            order.Add(1);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ordered (value INTEGER); INSERT INTO ordered VALUES (1);";
            command.ExecuteNonQuery();
            return connection;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = lane.ExecuteAsync(connection =>
        {
            order.Add(2);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM ordered;";
            return command.ExecuteScalar();
        });
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.Set();

        var disposedConnection = await first;
        Assert.Equal(System.Data.ConnectionState.Closed, disposedConnection.State);
        Assert.Equal(1L, await second);
        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task CancellationOfQueuedOrFullQueueWorkDoesNotExecuteIt()
    {
        await using var lane = new SqliteCommandLane(Factory, capacity: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = lane.ExecuteAsync(_ => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var queuedCancellation = new CancellationTokenSource();
        using var waitingCancellation = new CancellationTokenSource();
        var calls = 0;
        var queued = lane.ExecuteAsync(_ => Interlocked.Increment(ref calls), queuedCancellation.Token);
        var waiting = lane.ExecuteAsync(_ => Interlocked.Increment(ref calls), waitingCancellation.Token);
        Assert.False(waiting.IsCompleted);
        waitingCancellation.Cancel();
        queuedCancellation.Cancel();
        release.Set();

        Assert.Equal(1, await first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Assert.Equal(0, calls);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lane.ExecuteAsync(_ => 1, alreadyCanceled.Token));
    }

    [Fact]
    public async Task LateCancellationCannotHideACommittedResult()
    {
        await using var lane = new SqliteCommandLane(Factory);
        using var cancellation = new CancellationTokenSource();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var result = lane.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE committed (value INTEGER); INSERT INTO committed VALUES (7);";
            command.ExecuteNonQuery();
            committed.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            return 7;
        }, cancellation.Token);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.Set();

        Assert.Equal(7, await result);
        Assert.Equal(7L, await lane.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM committed;";
            return command.ExecuteScalar();
        }));
    }

    [Fact]
    public async Task AnOperationFailureDoesNotStopTheLane()
    {
        await using var lane = new SqliteCommandLane(Factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lane.ExecuteAsync<int>(_ => throw new InvalidOperationException("test failure")));
        Assert.Equal(42, await lane.ExecuteAsync(_ => 42));
    }

    [Fact]
    public async Task DisposalDrainsAcceptedWorkAndRejectsNewAndWaitingWork()
    {
        var lane = new SqliteCommandLane(Factory, capacity: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = lane.ExecuteAsync(_ => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var accepted = lane.ExecuteAsync(_ => 2);
        var waiting = lane.ExecuteAsync(_ => 3);
        var disposing = lane.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        release.Set();

        Assert.Equal(1, await first);
        Assert.Equal(2, await accepted);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await waiting);
        await disposing;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lane.ExecuteAsync(_ => 4));
        await lane.DisposeAsync();
    }

    [Fact]
    public async Task RejectsInvalidCapacityAndAsyncOperations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteCommandLane(Factory, capacity: 0));
        await using var lane = new SqliteCommandLane(Factory);
        await Assert.ThrowsAsync<ArgumentException>(() => lane.ExecuteAsync(_ => Task.FromResult(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => lane.ExecuteAsync(_ => new ValueTask<int>(1)));
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
}
