using SnuWatcher.Application.Sources;

namespace SnuWatcher.Infrastructure.Snu.Gateway;

/// <summary>Maps typed outcomes only. Raw server/exception messages never cross the application port.</summary>
public static class SourceFailureMapper
{
    public static SourceProblem Map(SourceFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return Map(failure.Kind, failure.RetryAfter);
    }

    public static SourceProblem Map(SnuSourceException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return Map(failure.Kind, failure.RetryAfter);
    }

    private static SourceProblem Map(SourceFailureKind kind, TimeSpan? retryAfter)
    {
        var mapped = kind switch
        {
            SourceFailureKind.Unavailable => (SourceProblemKind.Unavailable, "source_unavailable"),
            SourceFailureKind.AccessRestricted => (SourceProblemKind.AccessRestricted, "source_access_restricted"),
            SourceFailureKind.RateLimited => (SourceProblemKind.RateLimited, "source_rate_limited"),
            SourceFailureKind.ResponseTooLarge => (SourceProblemKind.ResponseTooLarge, "source_response_too_large"),
            SourceFailureKind.LimitReached => (SourceProblemKind.LimitReached, "source_limit_reached"),
            SourceFailureKind.Maintenance => (SourceProblemKind.Maintenance, "source_maintenance"),
            _ => (SourceProblemKind.ParseChanged, "source_parse_changed"),
        };
        // Past HTTP-date values mean no additional delay, rather than an invalid application DTO.
        return new(mapped.Item1, mapped.Item2, retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
    }
}
