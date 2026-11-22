using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Windows.Tests.Hosting;

internal sealed class HostTestSource : ICourseSource, IAsyncDisposable
{
    internal int Calls;
    internal bool Disposed;
    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken cancellationToken)
    { Interlocked.Increment(ref Calls); return Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
        new(query, [new(query.Year, "U000200002U000300002", "Validation term", null)], []), [], DateTimeOffset.UtcNow)); }
    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        var key = new SectionKey(query.Year, "U000200002", "U000300002", query.CourseCode!, "012", "0");
        return Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete, new(query,
            [new(key, "Private validation course", null, null, null, 119, 120, null, "Enrollment/Quota", DateTimeOffset.UtcNow)], 1), [], DateTimeOffset.UtcNow));
    }
    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken cancellationToken)
    { Interlocked.Increment(ref Calls); return Task.FromResult(new SourceRead<CourseDetails>(SourceReadStatus.Failed, null,
        [new(SourceProblemKind.Unavailable, "validation_details_unused")], DateTimeOffset.UtcNow)); }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
