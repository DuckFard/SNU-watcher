using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Snu.Gateway;

/// <summary>
/// Application gateway over one shared source. Only complete metadata is cached; search counts are
/// never cached. Every watch search starts its own source operation after this call's admission and
/// catalog confirmation. The host owns the supplied source and disposes it after this gateway drains.
/// </summary>
public sealed partial class SnuCourseGateway : ICourseSource, IAsyncDisposable
{
    private readonly SnuCourseSource _source;
    private readonly TimeProvider _clock;
    private readonly SnuGatewayOptions _options;
    private readonly object _sync = new();
    private readonly MetadataCache<CatalogQuery, CourseCatalogData> _catalogCache;
    private readonly MetadataCache<SectionKey, CourseDetails> _detailsCache;

    public SnuCourseGateway(SnuCourseSource source, TimeProvider clock, SnuGatewayOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new();
        _catalogCache = new(_options.CatalogCapacity, _options.MetadataTtl, clock);
        _detailsCache = new(_options.DetailsCapacity, _options.MetadataTtl, clock);
    }

    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePurpose(purpose);
        return AdmitAsync(cancellationToken, () => CatalogAsync(query, purpose, cancellationToken));
    }

    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidatePurpose(purpose);
        return AdmitAsync(cancellationToken, () => MetadataAsync(key, _detailsCache, _detailFlights,
            token => ReadDetailsAsync(key, purpose, token), cancellationToken));
    }

    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePurpose(purpose);
        return AdmitAsync(cancellationToken, () => SearchAsyncCore(query, purpose, cancellationToken));
    }

    private Task<SourceRead<CourseCatalogData>> CatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken token) =>
        MetadataAsync(query, _catalogCache, _catalogFlights, ct => ReadCatalogAsync(query, purpose, ct), token);

    private async Task<SourceRead<CourseSearchData>> SearchAsyncCore(CourseSearchQuery query, SourceRequestPurpose purpose,
        CancellationToken token)
    {
        // Resolve metadata before reserving the count flight. One caller/one flight budgets remain usable,
        // and this internal metadata waiter does not consume a second public caller admission.
        var catalog = await CatalogAsync(new(query.Year), purpose, token).ConfigureAwait(false);
        if (catalog.Data is null)
            return new(SourceReadStatus.Failed, null, catalog.Problems, catalog.CompletedAtUtc);
        if (!SnuIdentityMapper.TryCreateSearchQuery(query, catalog.Data, out var dispatched))
            return Failure<CourseSearchData>(new(SourceProblemKind.TermUnavailable, "term_unconfirmed"));
        return await SearchFlightAsync(query, purpose,
            ct => ReadSearchAsync(query, dispatched, purpose, ct), token).ConfigureAwait(false);
    }

    private async Task<SourceRead<CourseCatalogData>> ReadCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken token)
    {
        var raw = await _source.GetCatalogAsync(query.Year, purpose, query.TermSelector,
            query.UpDepartment, query.Department, token).ConfigureAwait(false);
        return SnuDataMapper.Catalog(query, raw, _clock.GetUtcNow());
    }

    private async Task<SourceRead<CourseDetails>> ReadDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken token)
    {
        var raw = await _source.GetDetailsAsync(SnuIdentityMapper.ToSourceIdentity(key), purpose, token).ConfigureAwait(false);
        return SnuDataMapper.Details(key, raw, _clock.GetUtcNow());
    }

    private async Task<SourceRead<CourseSearchData>> ReadSearchAsync(CourseSearchQuery query, SourceSearchQuery dispatched,
        SourceRequestPurpose purpose, CancellationToken token)
    {
        var raw = await _source.SearchAsync(dispatched, purpose, token).ConfigureAwait(false);
        return SnuDataMapper.Search(query, dispatched, raw, _clock.GetUtcNow());
    }

    private async Task<SourceRead<T>> GuardSourceAsync<T>(Func<CancellationToken, Task<SourceRead<T>>> operation,
        CancellationToken token) where T : class
    {
        try
        {
            token.ThrowIfCancellationRequested();
            return await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure<T>(new(SourceProblemKind.Unavailable, "source_unavailable")); }
        catch (SnuSourceException error) { return Failure<T>(SourceFailureMapper.Map(error)); }
        catch (IOException) { return Failure<T>(new(SourceProblemKind.Unavailable, "source_unavailable")); }
        // A disposed injected dependency is a host composition error. Other unexpected remote validation
        // failures expose no exception text, raw response, headers or credentials through the port.
        catch (Exception error) when (error is not ObjectDisposedException and not OutOfMemoryException)
        { return Failure<T>(new(SourceProblemKind.ParseChanged, "source_parse_changed")); }
    }

    private SourceRead<T> Failure<T>(SourceProblem problem) where T : class =>
        new(SourceReadStatus.Failed, null, [problem], _clock.GetUtcNow());

    private SourceRead<T> Busy<T>() where T : class => Failure<T>(new(SourceProblemKind.LimitReached, "gateway_busy"));

    private static void ValidatePurpose(SourceRequestPurpose purpose)
    {
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
    }
}
