namespace SnuWatcher.Infrastructure.Snu.RequestQueue;

internal sealed partial class SnuRequestDispatcher
{
    private abstract class WorkItem(CancellationToken callerToken)
    {
        public CancellationToken CallerToken { get; } = callerToken;
        public LinkedListNode<WorkItem>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public abstract Task ExecuteAsync(CancellationToken stopping, Action<SnuSourceException> applyFailure);
        public abstract void Cancel();
        public abstract void Fail(Exception error);
    }

    private sealed class WorkItem<T>(Func<CancellationToken, Task<T>> operation, CancellationToken callerToken) : WorkItem(callerToken)
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task ExecuteAsync(CancellationToken stopping, Action<SnuSourceException> applyFailure)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(CallerToken, stopping);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await operation(linked.Token).ConfigureAwait(false);
                // A completed operation owns its result even if cancellation arrived at the same boundary.
                Completion.TrySetResult(result);
            }
            catch (SnuSourceException error)
            {
                applyFailure(error); // Update shared policy before completing the caller or releasing this slot.
                Completion.TrySetException(error);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { Cancel(); }
            catch (Exception error) { Completion.TrySetException(error); }
        }

        public override void Cancel() => Completion.TrySetCanceled(CallerToken.IsCancellationRequested ? CallerToken : new CancellationToken(true));
        public override void Fail(Exception error) => Completion.TrySetException(error);
    }
}
