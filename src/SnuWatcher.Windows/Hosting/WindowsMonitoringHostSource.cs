using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Sources;
using SnuWatcher.Infrastructure.Persistence;

namespace SnuWatcher.Windows.Hosting;

internal sealed class WindowsMonitoringHostFactories(
    Func<RuntimeProfile, TimeProvider, CancellationToken, Task<SqliteWatchStore>> openStore,
    Func<SqliteWatchStore, TimeProvider, CancellationToken, Task<WindowsMonitoringHostSource>> openSource)
{
    internal Func<RuntimeProfile, TimeProvider, CancellationToken, Task<SqliteWatchStore>> OpenStore { get; } = openStore;
    internal Func<SqliteWatchStore, TimeProvider, CancellationToken, Task<WindowsMonitoringHostSource>> OpenSource { get; } = openSource;
}

internal sealed class WindowsMonitoringHostSource(ICourseSource courses, Func<ValueTask> closeGateway, Func<ValueTask> closeSource,
    Func<string?>? diagnostic = null, Func<CancellationToken, Task>? recover = null,
    Func<long, CancellationToken, Task<SourceAccessResumeResult>>? resume = null)
{
    internal ICourseSource Courses { get; } = courses;
    internal Func<ValueTask> CloseGateway { get; } = closeGateway;
    internal Func<ValueTask> CloseSource { get; } = closeSource;
    internal Func<string?> Diagnostic { get; } = diagnostic ?? (() => null);
    internal Func<CancellationToken, Task> Recover { get; } = recover ?? (_ => Task.CompletedTask);
    internal Func<long, CancellationToken, Task<SourceAccessResumeResult>> Resume { get; } = resume ??
        ((_, _) => Task.FromException<SourceAccessResumeResult>(new InvalidOperationException("This private source has no access-resume control.")));

    internal static WindowsMonitoringHostSource FromFake(ICourseSource source) => new(source, () => ValueTask.CompletedTask,
        () => source is IAsyncDisposable asyncOwner ? asyncOwner.DisposeAsync() : CloseSynchronous(source));
    private static ValueTask CloseSynchronous(ICourseSource source)
    { (source as IDisposable)?.Dispose(); return ValueTask.CompletedTask; }
}
