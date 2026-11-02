using SnuWatcher.Core;

namespace SnuWatcher.Application.Sources;

/// <summary>Fairness hint only; purpose never changes query identity or bypasses source policy.</summary>
public enum SourceRequestPurpose { Interactive, Watch }

/// <summary>
/// Anonymous bounded catalog, search and detail gateway. Implementations confirm raw term selectors
/// against source catalog options, preserve row receipt times and never fabricate identity aliases.
/// Caller cancellation throws; source failures return sanitized typed results. Source requests and
/// waits run outside persistence transactions and outside the lifecycle/send semaphore.
/// </summary>
public interface ICourseSource
{
    Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query,
        SourceRequestPurpose purpose, CancellationToken cancellationToken);
    Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query,
        SourceRequestPurpose purpose, CancellationToken cancellationToken);
    Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key,
        SourceRequestPurpose purpose, CancellationToken cancellationToken);
}
