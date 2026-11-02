using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Snu.Policy;

namespace SnuWatcher.Infrastructure.Snu;

public sealed partial class SnuCourseSource
{
    private readonly StoredSourceRequestPolicy? storedPolicy;

    /// <summary>Required hosted composition. The source owns its anonymous client/worker, never the policy store.</summary>
    public static Task<SnuCourseSource> CreateAsync(ISourcePolicyStore policyStore, SnuSourceOptions? options = null,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        return CreateStoredAsync(AnonymousClient(), policyStore, options ?? SnuSourceOptions.Default,
            timeProvider ?? TimeProvider.System, false, cancellationToken);
    }

    internal static Task<SnuCourseSource> CreateStoredForTestingAsync(HttpClient httpClient, ISourcePolicyStore policyStore,
        SnuSourceOptions options, TimeProvider timeProvider, Func<Task>? afterScopeReleased = null) =>
        CreateStoredAsync(httpClient, policyStore, options, timeProvider, true, default, afterScopeReleased);

    private static async Task<SnuCourseSource> CreateStoredAsync(HttpClient client, ISourcePolicyStore policyStore,
        SnuSourceOptions options, TimeProvider clock, bool testing, CancellationToken ct, Func<Task>? afterScopeReleased = null)
    {
        SnuCourseSource? source = null;
        try
        {
            ArgumentNullException.ThrowIfNull(policyStore);
            source = new(client, options, clock, null, testing, policyStore, afterScopeReleased);
            await source.storedPolicy!.InitializeAsync(options.RequestSpacing, ct).ConfigureAwait(false);
            return source;
        }
        catch
        {
            if (source is not null) await source.DisposeAsync().ConfigureAwait(false);
            else client.Dispose();
            throw;
        }
    }

    public string? SourcePolicyDiagnosticCode => storedPolicy?.DiagnosticCode;
    public Task RecoverSourcePolicyAsync(CancellationToken cancellationToken = default) =>
        (storedPolicy ?? throw new InvalidOperationException("This diagnostic source has no saved policy.")).RecoverAsync(cancellationToken);
    public Task<SourceAccessResumeResult> ResumeSourceAccessAsync(long expectedRevision,
        CancellationToken cancellationToken = default) =>
        (storedPolicy ?? throw new InvalidOperationException("This diagnostic source has no saved policy.")).ResumeAsync(expectedRevision, cancellationToken);
}
