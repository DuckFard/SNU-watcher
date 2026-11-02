namespace SnuWatcher.Windows.Hosting;

/// <summary>Retains one actual teardown attempt; only an explicit retry can replace a completed failure.</summary>
public sealed class RetryableTeardown
{
    private readonly object sync = new();
    private readonly Func<Task> operation;
    private Task? attempt;

    /// <summary>The operation must finish its actual owned work before completing or reporting a failure.</summary>
    public RetryableTeardown(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        this.operation = operation;
    }

    /// <summary>Shares an active or successful attempt, preserving failure evidence for each waiter.</summary>
    public Task RunAsync(bool retryFailed = false)
    {
        TaskCompletionSource completion;
        lock (sync)
        {
            if (attempt is not null && !(retryFailed && attempt.IsCompleted && !attempt.IsCompletedSuccessfully))
                return attempt;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Publish ownership before invoking a callback which could synchronously reenter this owner.
            attempt = completion.Task;
        }
        _ = CompleteAttemptAsync(completion);
        return completion.Task;
    }

    /// <summary>Maps ordinary teardown failure to a blocked result; fatal memory failure still propagates.</summary>
    public async Task<bool> TryRunAsync(bool retryFailed = false)
    {
        try { await RunAsync(retryFailed).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private async Task CompleteAttemptAsync(TaskCompletionSource completion)
    {
        try { await operation().ConfigureAwait(false); completion.TrySetResult(); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
