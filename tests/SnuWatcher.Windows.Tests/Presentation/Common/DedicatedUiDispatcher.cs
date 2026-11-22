using System.Collections.Concurrent;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Tests.Presentation.Common;

internal sealed class DedicatedUiDispatcher : IUiDispatcher, IAsyncDisposable
{
    private readonly BlockingCollection<Action> queue = [];
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int failNextInvoke;
    private int threadId;

    internal DedicatedUiDispatcher()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "SnuWatcher.Private.UiCommonTests" };
        thread.Start();
        ready.Task.Wait(TimeSpan.FromSeconds(5));
    }

    internal int ThreadId => threadId;
    internal bool FailPost { get; set; }
    internal void FailNextInvoke() => Interlocked.Exchange(ref failNextInvoke, 1);
    public bool CheckAccess() => Environment.CurrentManagedThreadId == threadId;

    public void Post(Action action)
    {
        if (FailPost) throw new InvalidOperationException("private dispatcher post detail");
        queue.Add(action);
    }

    public Task InvokeAsync(Action action, CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref failNextInvoke, 0) == 1)
            return Task.FromException(new InvalidOperationException("private dispatcher invoke detail"));
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            if (token.IsCancellationRequested) { completion.TrySetCanceled(token); return; }
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    private void Run()
    {
        threadId = Environment.CurrentManagedThreadId;
        ready.SetResult();
        try { foreach (var action in queue.GetConsumingEnumerable()) action(); stopped.SetResult(); }
        catch (Exception error) { stopped.TrySetException(error); }
    }

    public async ValueTask DisposeAsync()
    {
        queue.CompleteAdding();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Dispose();
    }
}
