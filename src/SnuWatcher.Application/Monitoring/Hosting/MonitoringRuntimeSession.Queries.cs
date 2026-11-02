using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Hosting;

public sealed partial class MonitoringRuntimeSession
{
    /// <summary>Only a complete source catalog may replace offline display hints; cached hints are never source authority.</summary>
    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return AdmitUiOperation(token => GetCatalogOwnedAsync(query, token), cancellationToken);
    }

    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return AdmitUiOperation(token => ReadSourceAsync(
            () => _interactiveSource.SearchAsync(query, SourceRequestPurpose.Interactive, token),
            data => data.Query == query, token), cancellationToken);
    }

    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return AdmitUiOperation(token => ReadSourceAsync(
            () => _interactiveSource.GetDetailsAsync(key, SourceRequestPurpose.Interactive, token),
            data => data.Key == key, token), cancellationToken);
    }

    private async Task<SourceRead<CourseCatalogData>> GetCatalogOwnedAsync(CatalogQuery query, CancellationToken token)
    {
        var cache = _presentationServices?.TermHints;
        TermHintCacheSnapshot? beforeQuery = cache is null ? null
            : await RunPresentationAsync(() => cache.ReadAsync(token), token).ConfigureAwait(false);
        var result = await ReadSourceAsync(
            () => _interactiveSource.GetCatalogAsync(query, SourceRequestPurpose.Interactive, token),
            data => data.Query == query, token).ConfigureAwait(false);
        if (cache is not null && beforeQuery is not null && result.Status == SourceReadStatus.Complete)
        {
            var command = new ReplaceTermHintsCommand(beforeQuery.Revision, query.Year, result.CompletedAtUtc, result.Data!.Terms);
            // A concurrent cache replacement wins. Do not reread/retry with a newer revision or
            // replace the original result time with local completion/cache-save time.
            await RunPresentationAsync(() => cache.TryReplaceYearAsync(command, token), token).ConfigureAwait(false);
        }
        return result;
    }

    private async Task<SourceRead<T>> ReadSourceAsync<T>(Func<Task<SourceRead<T>>> operation,
        Func<T, bool> matchesQuery, CancellationToken token) where T : class
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await operation().ConfigureAwait(false);
            if (result is null || result.Data is { } data && !matchesQuery(data)) throw new InvalidOperationException();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _hub.ReportFailure(RuntimeFailureKind.Actor, "runtime_query_failed");
            throw new InvalidOperationException("The runtime could not complete the source query.");
        }
        finally
        {
            // The gateway may return a typed source failure for a local policy-storage failure.
            // Check its sticky diagnostic even with no watches; wake reloads current durable policy.
            _hub.CheckSourceDiagnostic();
            Wake();
        }
    }
}
