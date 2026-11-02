using System.Collections.Concurrent;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Sources;
using SnuWatcher.Application.Tests.Delivery.Batches;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

/// <summary>Scripted boundary responses; every logical query is retained until its handler returns.</summary>
internal sealed class InteractiveRuntimeFixture : IAsyncDisposable
{
    internal InteractiveRuntimeFixture(RuntimeSessionOptions? options = null,
        Func<string?>? diagnostic = null, RuntimePresentationServices? presentation = null)
    {
        Mutations = new(Store);
        Session = new(Mutations, new FakeMonitorPolicy(), Batches, Work, Recovery,
            Source, Channel, Clock, options ?? new(), Gate, diagnostic, presentation);
    }
    internal FakeMonitorStore Store { get; } = new();
    internal ScriptedRuntimeMutations Mutations { get; }
    internal ScriptedRuntimeCourseSource Source { get; } = new();
    internal SplitMonitorClock Clock { get; } = new();
    internal BatchPort Batches { get; } = new();
    internal BatchChannel Channel { get; } = new();
    internal CountingRuntimeWork Work { get; } = new();
    internal RuntimeRecoveryDiscovery Recovery { get; } = new();
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal MonitoringRuntimeSession Session { get; }
    public async ValueTask DisposeAsync() { await Session.DisposeAsync(); Gate.Dispose(); }
}

internal sealed class CountingRuntimeWork : IPendingDeliveryWorkReader
{
    internal int Reads => Volatile.Read(ref _reads);
    private int _reads;
    public Task<PendingDeliveryWorkPage> ReadPageAsync(PendingDeliveryWorkCursor? after, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _reads);
        return Task.FromResult(new PendingDeliveryWorkPage([], null, false));
    }
}

internal sealed record RuntimeSourceInvocation(object Query, SourceRequestPurpose Purpose, CancellationToken Token);

internal sealed class ScriptedRuntimeCourseSource : ICourseSource
{
    internal ConcurrentQueue<RuntimeSourceInvocation> Calls { get; } = new();
    internal Func<CatalogQuery, CancellationToken, Task<SourceRead<CourseCatalogData>>> Catalog { get; set; } =
        (query, _) => Task.FromResult(CatalogResult(query));
    internal Func<CourseSearchQuery, CancellationToken, Task<SourceRead<CourseSearchData>>> Search { get; set; } =
        (query, _) => Task.FromResult(MonitorFixture.Rows(query, [MonitorFixture.Key()], MonitorFixture.Now));
    internal Func<SectionKey, CancellationToken, Task<SourceRead<CourseDetails>>> Details { get; set; } =
        (_, _) => Task.FromResult(Failure<CourseDetails>());

    internal static SourceRead<CourseCatalogData> CatalogResult(CatalogQuery query, DateTimeOffset? at = null) => new(
        SourceReadStatus.Complete,
        new(query, [new CatalogTerm(query.Year, "21", "2학기", "Second semester")], []), [], at ?? MonitorFixture.Now);
    internal static SourceRead<T> Failure<T>() where T : class => new(SourceReadStatus.Failed, null,
        [new SourceProblem(SourceProblemKind.Unavailable, "source_unavailable")], MonitorFixture.Now);

    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken token)
    { Calls.Enqueue(new(query, purpose, token)); return Catalog(query, token); }
    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken token)
    { Calls.Enqueue(new(query, purpose, token)); return Search(query, token); }
    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken token)
    { Calls.Enqueue(new(key, purpose, token)); return Details(key, token); }
}
