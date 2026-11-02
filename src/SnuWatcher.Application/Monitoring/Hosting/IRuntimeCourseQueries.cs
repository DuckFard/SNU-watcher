using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Hosting;

/// <summary>
/// Interactive reads through the same hosted gateway used by monitoring. UI cancellation cancels
/// only that gateway waiter; the session retains its logical task and the host drains physical flights.
/// </summary>
public interface IRuntimeCourseQueries
{
    Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, CancellationToken cancellationToken = default);
    Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, CancellationToken cancellationToken = default);
    Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, CancellationToken cancellationToken = default);
}
