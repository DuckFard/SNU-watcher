using SnuWatcher.Application.Monitoring.SourcePolicy;

namespace SnuWatcher.Infrastructure.Snu.Policy;

/// <summary>One callback owns this context and replaces its immutable facts; no source-global timestamp is consulted.</summary>
internal sealed class SourceTransportContext(Action? beforeStart = null)
{
    private SourceTransportFacts facts = new();
    public void BeforeStart() => beforeStart?.Invoke();
    public void Started(DateTimeOffset value) => facts = facts with { ActualStartedAtUtc = value.ToUniversalTime() };
    public void Headers(DateTimeOffset value, int status) => facts = facts with { HeadersReceivedAtUtc = value.ToUniversalTime(), HttpStatusCode = status };
    public void Body(DateTimeOffset value) => facts = facts with { BodyReceivedAtUtc = value.ToUniversalTime() };
    public void NetworkFailure() => facts = facts with { FailureCause = SourceRequestOutcome.NetworkFailure };
    public void Timeout() => facts = facts with { FailureCause = SourceRequestOutcome.Unavailable };
    public void Classified(SnuSourceException failure) => facts = facts with { ClassifiedFailure = failure };
    public SnuSourceException? ClassifiedFailure => facts.ClassifiedFailure;

    public SourceRequestResultCommand Command(IssuedSourceRequest issued, DateTimeOffset completedAtUtc, SnuSourceException? error = null)
    {
        var outcome = error?.Kind switch
        {
            null => SourceRequestOutcome.Success,
            SourceFailureKind.RateLimited => SourceRequestOutcome.RateLimited,
            SourceFailureKind.AccessRestricted => SourceRequestOutcome.AccessRestricted,
            SourceFailureKind.Maintenance => SourceRequestOutcome.Maintenance,
            SourceFailureKind.Unavailable when error.HttpStatusCode == 503 => SourceRequestOutcome.ServiceUnavailable,
            SourceFailureKind.Unavailable => facts.FailureCause ?? SourceRequestOutcome.Unavailable,
            _ => SourceRequestOutcome.ParseChanged
        };
        return new(issued.Token, outcome, completedAtUtc, facts.ActualStartedAtUtc, facts.HeadersReceivedAtUtc,
            error?.RetryAfter is { } delay && facts.HeadersReceivedAtUtc is not null ? SourceRetryAfter.ResolvedDelay(delay) : null,
            error is null ? null : outcome switch
            {
                SourceRequestOutcome.RateLimited => "http_429",
                SourceRequestOutcome.AccessRestricted => "access_restricted",
                SourceRequestOutcome.ServiceUnavailable => "http_503",
                SourceRequestOutcome.NetworkFailure => "network_unavailable",
                SourceRequestOutcome.Maintenance => "source_maintenance",
                SourceRequestOutcome.ParseChanged => "source_parse_changed",
                _ => "source_unavailable"
            });
    }
}

internal sealed record SourceTransportFacts(DateTimeOffset? ActualStartedAtUtc = null,
    DateTimeOffset? HeadersReceivedAtUtc = null, DateTimeOffset? BodyReceivedAtUtc = null,
    int? HttpStatusCode = null, SourceRequestOutcome? FailureCause = null, SnuSourceException? ClassifiedFailure = null);
