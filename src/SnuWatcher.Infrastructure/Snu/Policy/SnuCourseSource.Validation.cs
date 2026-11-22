using SnuWatcher.Application.Monitoring.SourcePolicy;

namespace SnuWatcher.Infrastructure.Snu;

public sealed partial class SnuCourseSource
{
    /// <summary>
    /// Owned hosted transport injection for explicitly marked offline qualification. The caller supplies
    /// a no-socket handler; normal source spacing, durable policy, parsers and transport limits remain active.
    /// Ownership of the client transfers to this source, including construction failure.
    /// </summary>
    public static Task<SnuCourseSource> CreateWithTransportAsync(HttpClient httpClient, ISourcePolicyStore policyStore,
        SnuSourceOptions? options = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient); ArgumentNullException.ThrowIfNull(policyStore);
        return CreateStoredAsync(httpClient, policyStore, options ?? SnuSourceOptions.Default,
            timeProvider ?? TimeProvider.System, false, cancellationToken);
    }
}
