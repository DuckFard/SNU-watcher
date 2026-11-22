using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

/// <summary>Persisted issuance identity and global order; wall-clock time never orders completions.</summary>
public sealed record CheckAttemptToken
{
    public CheckAttemptToken(Guid id, long ordinal)
    {
        Id = PersistenceValidation.Id(id, nameof(id));
        Ordinal = PersistenceValidation.Positive(ordinal, nameof(ordinal));
    }

    public Guid Id { get; }
    public long Ordinal { get; }
}

public sealed record CheckIssue
{
    public CheckIssue(DateTimeOffset issuedAtUtc, IEnumerable<SectionKey> scope)
    {
        IssuedAtUtc = issuedAtUtc.ToUniversalTime();
        Scope = PersistenceValidation.Sections(scope);
    }

    public DateTimeOffset IssuedAtUtc { get; }
    public ImmutableArray<SectionKey> Scope { get; }
}

/// <summary>Issuance and the confirmed immutable scope are inserted in the same transaction.</summary>
public sealed record IssuedCheck
{
    public IssuedCheck(CheckAttemptToken token, DateTimeOffset issuedAtUtc, IEnumerable<SectionKey> scope)
    {
        ArgumentNullException.ThrowIfNull(token);
        Token = token;
        IssuedAtUtc = issuedAtUtc.ToUniversalTime();
        Scope = PersistenceValidation.Sections(scope);
    }

    public CheckAttemptToken Token { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public ImmutableArray<SectionKey> Scope { get; }
}

public enum CheckOutcome { Success, Unavailable, RateLimited, AccessRestricted, ParseChanged, NotFound, Incomplete }

/// <summary>Immutable source facts used for replay identity, separate from recomputable effects and versions.</summary>
public sealed record SectionCompletion
{
    public SectionCompletion(CheckAttemptToken token, SeatObservation observation, CheckOutcome outcome, string? diagnosticCode = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Key);
        PersistenceValidation.Defined(outcome, nameof(outcome));
        if (observation.AttemptSequence != token.Ordinal)
            throw new ArgumentException("Observation order must match the issued token.", nameof(observation));
        if ((observation.Kind == ObservationKind.Valid) != (outcome == CheckOutcome.Success))
            throw new ArgumentException("Only a valid observation has a successful outcome.", nameof(outcome));
        if (diagnosticCode is not null && (diagnosticCode.Length is < 1 or > 64
            || diagnosticCode.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))))
            throw new ArgumentException("Diagnostics use a bounded code, never raw response or exception text.", nameof(diagnosticCode));
        Token = token;
        Observation = observation;
        Outcome = outcome;
        DiagnosticCode = diagnosticCode;
    }

    public CheckAttemptToken Token { get; }
    public SeatObservation Observation { get; }
    public SectionKey Key => Observation.Key;
    public DateTimeOffset ObservedAtUtc => Observation.ObservedAt.ToUniversalTime();
    public CheckOutcome Outcome { get; }
    public string? DiagnosticCode { get; }
}
