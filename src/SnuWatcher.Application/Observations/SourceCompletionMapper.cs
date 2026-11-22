using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Observations;

/// <summary>
/// Reduces a completed exact-course source read to the immutable scope issued before dispatch.
/// This pure boundary performs no I/O and does not catch or manufacture caller cancellation.
/// Source-wide cooldowns belong upstream; a later failure must not poison verified earlier rows.
/// </summary>
public static class SourceCompletionMapper
{
    public static ImmutableArray<SectionCompletion> Map(IssuedCheck issued, CourseSearchQuery query,
        SourceRead<CourseSearchData> read)
    {
        ArgumentNullException.ThrowIfNull(issued);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(read);
        ValidateScope(issued, query);
        if (read.Data is { } data && data.Query != query)
            throw new ArgumentException("Source data must identify the exact requested query.", nameof(read));

        var rows = read.Data?.Rows.ToDictionary(row => row.Key);
        return issued.Scope.Select(key => rows is not null && rows.TryGetValue(key, out var row)
            ? FromRow(issued, key, row) : Missing(issued, key, read)).ToImmutableArray();
    }

    private static void ValidateScope(IssuedCheck issued, CourseSearchQuery query)
    {
        if (query.CourseCode is null || query.Name is not null || query.Instructor is not null
            || query.UpDepartment is not null || query.Department is not null)
            throw new ArgumentException("Watch searches require an exact course code without display filters.", nameof(query));
        // Validate the catalog's opaque selector against confirmed full keys; never split or infer it.
        if (issued.Scope.Any(key => key.Year != query.Year || key.CourseCode != query.CourseCode
            || key.SemesterCode + key.DetailSemesterCode != query.TermSelector))
            throw new ArgumentException("Every issued key must belong to the requested exact course and term.", nameof(issued));
    }

    private static SectionCompletion FromRow(IssuedCheck issued, SectionKey key, CourseSearchRow row)
    {
        // Additional fail-closed guard, not proof of fresh request provenance or a replacement for
        // ordinal/generation ordering. Clock rollback can withhold this attempt. Upstream must still
        // prohibit replaying cached counts or joining a request started before watch issuance.
        if (row.ObservedAtUtc < issued.IssuedAtUtc)
            return new(issued.Token, SeatObservation.Unknown(key, issued.Token.Ordinal, row.ObservedAtUtc),
                CheckOutcome.Incomplete, "observation_before_attempt");
        var observation = SeatObservation.FromSource(key, issued.Token.Ordinal, row.ObservedAtUtc,
            row.Registered, row.Capacity, row.QuotaLabel);
        return observation.Kind switch
        {
            ObservationKind.Valid => new(issued.Token, observation, CheckOutcome.Success),
            ObservationKind.SemanticsUnverified => new(issued.Token, observation, CheckOutcome.ParseChanged, "quota_unverified"),
            _ => new(issued.Token, observation, CheckOutcome.ParseChanged, "invalid_counts"),
        };
    }

    private static SectionCompletion Missing(IssuedCheck issued, SectionKey key, SourceRead<CourseSearchData> read)
    {
        var observation = SeatObservation.Unknown(key, issued.Token.Ordinal, read.CompletedAtUtc);
        if (read.Status == SourceReadStatus.Complete)
            return new(issued.Token, observation, CheckOutcome.NotFound, "section_not_found");
        // SourceRead guarantees at least one problem for Partial/Failed. First-problem precedence is
        // deterministic per section; the shared request policy separately honors all cooldown problems.
        var problem = read.Problems[0];
        var code = problem.Kind == SourceProblemKind.Maintenance ? "source_maintenance" : problem.DiagnosticCode;
        var outcome = read.Status == SourceReadStatus.Partial ? CheckOutcome.Incomplete : problem.Kind switch
        {
            SourceProblemKind.RateLimited => CheckOutcome.RateLimited,
            SourceProblemKind.AccessRestricted => CheckOutcome.AccessRestricted,
            SourceProblemKind.ParseChanged => CheckOutcome.ParseChanged,
            SourceProblemKind.LimitReached => CheckOutcome.Incomplete,
            _ => CheckOutcome.Unavailable,
        };
        return new(issued.Token, observation, outcome, code);
    }
}
