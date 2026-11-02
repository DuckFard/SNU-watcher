using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Monitoring.SourcePolicy;

/// <summary>One shared policy identity for every official catalog, search page and detail request.</summary>
public static class SourcePolicyIds
{
    public const string SnuSugang = "snu-sugang-v1";
    internal static string Validate(string value) => value == SnuSugang ? value
        : throw new ArgumentException("The source policy identity is unsupported.", nameof(value));
}

public enum SourceRequestKind { Catalog, SearchPage, Detail }
public enum SourceRequestOutcome
{
    Success, RateLimited, Maintenance, AccessRestricted, ServiceUnavailable, Unavailable, NetworkFailure, ParseChanged,
}
public enum SourceRetryAfterKind { Delta, HttpDate, ResolvedDelay }

/// <summary>Typed server evidence. Its deadline is derived from receipt time, never persistence completion.</summary>
public sealed record SourceRetryAfter
{
    private SourceRetryAfter(SourceRetryAfterKind kind, TimeSpan? delta, DateTimeOffset? date)
    { Kind = kind; DeltaDelay = delta; HttpDateUtc = date?.ToUniversalTime(); }
    public SourceRetryAfterKind Kind { get; }
    public TimeSpan? DeltaDelay { get; }
    public DateTimeOffset? HttpDateUtc { get; }
    public static SourceRetryAfter Delta(TimeSpan value) => new(SourceRetryAfterKind.Delta,
        value < TimeSpan.Zero ? TimeSpan.Zero : value, null);
    public static SourceRetryAfter HttpDate(DateTimeOffset value) => new(SourceRetryAfterKind.HttpDate, null, value);
    /// <summary>Duration already resolved by the HTTP adapter; does not claim the original header format survived.</summary>
    public static SourceRetryAfter ResolvedDelay(TimeSpan value) => new(SourceRetryAfterKind.ResolvedDelay,
        value < TimeSpan.Zero ? TimeSpan.Zero : value, null);
    public TimeSpan DelayAt(DateTimeOffset receivedAtUtc)
    {
        var delay = DeltaDelay ?? HttpDateUtc!.Value - receivedAtUtc;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}

public sealed record SourceRequestIssue
{
    public SourceRequestIssue(string sourceId, SourceRequestKind kind)
    { SourceId = SourcePolicyIds.Validate(sourceId); Kind = PersistenceValidation.Defined(kind, nameof(kind)); }
    public string SourceId { get; }
    public SourceRequestKind Kind { get; }
}

public sealed record SourceRequestToken
{
    public SourceRequestToken(Guid id, string sourceId, long ordinal)
    {
        Id = PersistenceValidation.Id(id, nameof(id)); SourceId = SourcePolicyIds.Validate(sourceId);
        Ordinal = PersistenceValidation.Positive(ordinal, nameof(ordinal));
    }
    public Guid Id { get; }
    public string SourceId { get; }
    public long Ordinal { get; }
}

/// <summary>Durable intent. IssuedAtUtc does not claim that HTTP actually began.</summary>
public sealed record IssuedSourceRequest
{
    public IssuedSourceRequest(SourceRequestToken token, SourceRequestKind kind, DateTimeOffset issuedAtUtc, long resumeWatermark)
    {
        ArgumentNullException.ThrowIfNull(token); Token = token;
        Kind = PersistenceValidation.Defined(kind, nameof(kind)); IssuedAtUtc = issuedAtUtc.ToUniversalTime();
        ResumeWatermark = PersistenceValidation.Nonnegative(resumeWatermark, nameof(resumeWatermark));
        if (resumeWatermark >= token.Ordinal) throw new ArgumentOutOfRangeException(nameof(resumeWatermark));
    }
    public SourceRequestToken Token { get; }
    public SourceRequestKind Kind { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public long ResumeWatermark { get; }
}

public sealed record SourceRequestResultCommand
{
    public SourceRequestResultCommand(SourceRequestToken token, SourceRequestOutcome outcome, DateTimeOffset completedAtUtc,
        DateTimeOffset? actualStartedAtUtc = null, DateTimeOffset? receivedAtUtc = null,
        SourceRetryAfter? retryAfter = null, string? diagnosticCode = null)
    {
        ArgumentNullException.ThrowIfNull(token); Token = token;
        Outcome = PersistenceValidation.Defined(outcome, nameof(outcome)); CompletedAtUtc = completedAtUtc.ToUniversalTime();
        ActualStartedAtUtc = actualStartedAtUtc?.ToUniversalTime(); ReceivedAtUtc = receivedAtUtc?.ToUniversalTime();
        if (retryAfter is not null && receivedAtUtc is null)
            throw new ArgumentException("Retry-After evidence requires a transport receipt timestamp.", nameof(retryAfter));
        RetryAfter = retryAfter;
        if (diagnosticCode is not null && (diagnosticCode.Length is < 1 or > 64
            || diagnosticCode.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))))
            throw new ArgumentException("A diagnostic code needs 1-64 lowercase ASCII machine-code characters.", nameof(diagnosticCode));
        DiagnosticCode = diagnosticCode;
    }
    public SourceRequestToken Token { get; }
    public SourceRequestOutcome Outcome { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public DateTimeOffset? ActualStartedAtUtc { get; }
    public DateTimeOffset? ReceivedAtUtc { get; }
    public SourceRetryAfter? RetryAfter { get; }
    public string? DiagnosticCode { get; }
}

public sealed record SourceAccessResumeCommand
{
    public SourceAccessResumeCommand(string sourceId, long expectedRevision)
    {
        SourceId = SourcePolicyIds.Validate(sourceId);
        ExpectedRevision = PersistenceValidation.Nonnegative(expectedRevision, nameof(expectedRevision));
    }
    public string SourceId { get; }
    public long ExpectedRevision { get; }
}
