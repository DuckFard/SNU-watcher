using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Planning;

/// <summary>A detached planning result; it allocates no attempts and changes no watch lifecycle state.</summary>
public sealed record WatchQueryPlan
{
    internal WatchQueryPlan(ImmutableArray<PlannedWatchQuery> queries, ImmutableArray<BlockedWatchSection> blockedSections)
    {
        Queries = queries;
        BlockedSections = blockedSections;
    }

    public ImmutableArray<PlannedWatchQuery> Queries { get; }
    public ImmutableArray<BlockedWatchSection> BlockedSections { get; }
}

/// <summary>One logical source query and the exact six-part section identities it must serve.</summary>
public sealed record PlannedWatchQuery
{
    internal PlannedWatchQuery(CourseSearchQuery query, ImmutableArray<SectionKey> scope)
    {
        Query = query;
        Scope = scope;
        IssueScopes = scope.GroupBy(key => (key.Year, key.SemesterCode, key.DetailSemesterCode))
            .SelectMany(term => term.Chunk(PersistenceLimits.MaxSections))
            .Select(chunk => chunk.ToImmutableArray()).ToImmutableArray();
    }

    public CourseSearchQuery Query { get; }
    /// <summary>
    /// Logical membership, which can span distinct full-term components sharing a reported selector
    /// or exceed one persistence command's budget. Use IssueScopes for persistence issuance.
    /// </summary>
    public ImmutableArray<SectionKey> Scope { get; }
    /// <summary>Exact full-term scopes bounded for CheckIssue; all must be issued before the shared fresh source read.</summary>
    public ImmutableArray<ImmutableArray<SectionKey>> IssueScopes { get; }
}

/// <summary>An active section that cannot be queried until its source catalog confirms its term.</summary>
public sealed record BlockedWatchSection
{
    internal BlockedWatchSection(SectionKey key, string reasonCode)
    {
        Key = key;
        ReasonCode = reasonCode;
    }

    public SectionKey Key { get; }
    public string ReasonCode { get; }
}
