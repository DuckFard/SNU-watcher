using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Planning;

/// <summary>
/// Pure planning over store snapshots and source-reported catalogs. It never initializes members,
/// resets generations, issues tokens, fetches source data or changes persisted watch status.
/// </summary>
public static class WatchQueryPlanner
{
    private const int MaximumInputSubscriptions = 10_000;
    private const int MaximumInputCatalogs = 2200 - 2013 + 1;

    /// <summary>
    /// Union active sections into exact course queries, with visible blocked sections. The default
    /// 2000-section bound (configurable through 10000) limits this operation's resources; it is not
    /// a persistence capacity. Catalogs must contain at most one snapshot per academic year.
    /// </summary>
    public static WatchQueryPlan Plan(IEnumerable<SubscriptionSnapshot> subscriptions,
        IEnumerable<CourseCatalogData> catalogs, DateTimeOffset now, int maxUniqueSections = 2000)
    {
        if (maxUniqueSections is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maxUniqueSections));
        var watches = PersistenceValidation.Snapshot(subscriptions, MaximumInputSubscriptions, nameof(subscriptions));
        var sourceCatalogs = PersistenceValidation.Snapshot(catalogs, MaximumInputCatalogs, nameof(catalogs));
        if (sourceCatalogs.DistinctBy(catalog => catalog.Query.Year).Count() != sourceCatalogs.Length)
            throw new ArgumentException("Provide one unambiguous catalog per academic year.", nameof(catalogs));
        var catalogsByYear = sourceCatalogs.ToImmutableDictionary(catalog => catalog.Query.Year);
        var nowUtc = now.ToUniversalTime();
        var activeKeys = watches.Where(watch => watch.Status == WatchStatus.Active
                && watch.Expiry?.IsExpiredAt(nowUtc) != true)
            .SelectMany(watch => watch.Members.Select(member => member.Key))
            .Distinct().Take(maxUniqueSections + 1).ToImmutableArray();
        if (activeKeys.Length > maxUniqueSections) throw new WatchQueryPlanLimitException(maxUniqueSections);

        var resolved = OrderKeys(activeKeys).Select(key => Resolve(key, catalogsByYear)).ToImmutableArray();
        var blocked = resolved.Where(row => row.ReasonCode is not null)
            .Select(row => new BlockedWatchSection(row.Key, row.ReasonCode!)).ToImmutableArray();
        var queries = resolved.Where(row => row.Term is not null)
            .GroupBy(row => new CourseSearchQuery(row.Key.Year, row.Term!.Selector, row.Key.CourseCode))
            .OrderBy(group => group.Key.Year).ThenBy(group => group.Key.TermSelector, StringComparer.Ordinal)
            .ThenBy(group => group.Key.CourseCode, StringComparer.Ordinal)
            .Select(group => new PlannedWatchQuery(group.Key, OrderKeys(group.Select(row => row.Key)).ToImmutableArray()))
            .ToImmutableArray();
        return new WatchQueryPlan(queries, blocked);
    }

    private static (SectionKey Key, CatalogTerm? Term, string? ReasonCode) Resolve(SectionKey key,
        IReadOnlyDictionary<int, CourseCatalogData> catalogs)
    {
        if (!catalogs.TryGetValue(key.Year, out var catalog)) return (key, null, "catalog_unavailable");
        // Concatenation only tests a source-reported option. Never send an invented selector or infer aliases.
        var term = catalog.Terms.FirstOrDefault(option => string.Equals(option.Selector,
            string.Concat(key.SemesterCode, key.DetailSemesterCode), StringComparison.Ordinal));
        return term is null ? (key, null, "term_unconfirmed") : (key, term, null);
    }

    private static IOrderedEnumerable<SectionKey> OrderKeys(IEnumerable<SectionKey> keys) => keys.OrderBy(key => key.Year)
        .ThenBy(key => key.SemesterCode, StringComparer.Ordinal).ThenBy(key => key.DetailSemesterCode, StringComparer.Ordinal)
        .ThenBy(key => key.CourseCode, StringComparer.Ordinal).ThenBy(key => key.Section, StringComparer.Ordinal)
        .ThenBy(key => key.SubsubjectCode, StringComparer.Ordinal);
}
