using System.Collections.Immutable;

namespace SnuWatcher.Application.Sources;

public enum SourceReadStatus { Complete, Partial, Failed }
public enum SourceProblemKind
{
    Unavailable, RateLimited, AccessRestricted, ParseChanged,
    ResponseTooLarge, LimitReached, Maintenance, TermUnavailable
}

/// <summary>Sanitized machine-readable source outcome. Long server Retry-After values are not clamped.</summary>
public sealed record SourceProblem
{
    public SourceProblem(SourceProblemKind kind, string diagnosticCode, TimeSpan? retryAfter = null)
    {
        Kind = SourceValidation.Defined(kind, nameof(kind));
        DiagnosticCode = SourceValidation.Diagnostic(diagnosticCode);
        if (retryAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryAfter));
        RetryAfter = retryAfter;
    }

    public SourceProblemKind Kind { get; }
    public string DiagnosticCode { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Complete has verified data and no problems. Partial is supported only for search data, with verified
/// page coverage and a problem. Failed has no data and at least one problem. Caller cancellation must
/// propagate as cancellation rather than constructing a failed source result.
/// </summary>
public sealed record SourceRead<T> where T : class
{
    public SourceRead(SourceReadStatus status, T? data, IEnumerable<SourceProblem> problems, DateTimeOffset completedAtUtc)
    {
        Status = SourceValidation.Defined(status, nameof(status));
        Problems = SourceValidation.Snapshot(problems, SourceLimits.MaxProblems, nameof(problems));
        var valid = status switch
        {
            SourceReadStatus.Complete => data is not null && Problems.IsEmpty,
            SourceReadStatus.Partial => typeof(T) == typeof(CourseSearchData) && data is not null && !Problems.IsEmpty,
            _ => data is null && !Problems.IsEmpty,
        };
        if (!valid) throw new ArgumentException("Source result status, data and problems do not agree.");
        Data = data;
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
    }

    public SourceReadStatus Status { get; }
    public T? Data { get; }
    public ImmutableArray<SourceProblem> Problems { get; }
    public DateTimeOffset CompletedAtUtc { get; }
}
