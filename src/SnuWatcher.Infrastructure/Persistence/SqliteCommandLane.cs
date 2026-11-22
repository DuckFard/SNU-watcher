using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>
/// A bounded queue for synchronous database operations, consumed serially off the caller's thread.
/// Each operation owns one connection until completion and must materialize results before returning.
/// Cancellation can remove queued work; once execution begins its actual result is returned, including
/// after a commit, so late cancellation cannot turn committed work into an apparent cancellation.
/// </summary>
public sealed class SqliteCommandLane : IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Channel<IWorkItem> _queue;
    private readonly Task _worker;
    private int _closing;

    public SqliteCommandLane(SqliteConnectionFactory factory, int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _factory = factory;
        _queue = Channel.CreateBounded<IWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ConsumeAsync);
    }

    /// <summary>Enqueues one whole bounded operation. Async delegates are not supported.</summary>
    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RejectAsyncResult<T>();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
        var work = new WorkItem<T>(operation, cancellationToken);
        try
        {
            await _queue.Writer.WriteAsync(work, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            work.Abandon();
            throw new ObjectDisposedException(nameof(SqliteCommandLane));
        }
        catch
        {
            work.Abandon();
            throw;
        }
        return await work.Completion.ConfigureAwait(false);
    }

    /// <summary>Stops accepting work and drains already accepted operations before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0) _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false)) work.Execute(_factory);
    }

    private static void RejectAsyncResult<T>()
    {
        var type = typeof(T);
        if (typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            throw new ArgumentException("Database operations must finish synchronously within the command lane.", "operation");
    }

    private interface IWorkItem
    {
        void Execute(SqliteConnectionFactory factory);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<SqliteConnection, T> _operation;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        // 0 = queued, 1 = executing, 2 = canceled before execution, 3 = completed/abandoned.
        private int _state;

        public WorkItem(Func<SqliteConnection, T> operation, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationToken = cancellationToken;
            _registration = cancellationToken.Register(CancelQueuedWork);
        }

        public Task<T> Completion => _completion.Task;

        public void Execute(SqliteConnectionFactory factory)
        {
            if (_cancellationToken.IsCancellationRequested) CancelQueuedWork();
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                _registration.Dispose();
                return;
            }
            try
            {
                T result;
                using (var connection = factory.Open()) result = _operation(connection);
                _completion.TrySetResult(result);
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
            finally
            {
                Volatile.Write(ref _state, 3);
                _registration.Dispose();
            }
        }

        public void Abandon()
        {
            Interlocked.CompareExchange(ref _state, 3, 0);
            _registration.Dispose();
        }

        private void CancelQueuedWork()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                _completion.TrySetCanceled(_cancellationToken);
        }
    }
}
