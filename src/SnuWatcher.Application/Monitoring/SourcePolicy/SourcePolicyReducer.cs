namespace SnuWatcher.Application.Monitoring.SourcePolicy;

/// <summary>Pure ordered source-policy transitions; original transport UTC remains truthful across clock changes.</summary>
public static class SourcePolicyReducer
{
    public static SourcePolicyTransition Apply(SourcePolicySnapshot current, IssuedSourceRequest issued, SourceRequestResultCommand result)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(issued); ArgumentNullException.ThrowIfNull(result);
        if (issued.Token != result.Token || current.SourceId != result.Token.SourceId
            || result.Token.Ordinal > current.Progress.LastIssuedOrdinal)
            throw new ArgumentException("The result must match issued source intent.", nameof(result));
        if (result.Token.Ordinal <= current.Progress.LatestCompletedOrdinal) return new(current, false);
        var previous = current.Progress;
        var success = result.Outcome == SourceRequestOutcome.Success;
        var progress = new SourceRequestProgress(previous.LastIssuedOrdinal, previous.LastIssuedAtUtc,
            result.Token.Ordinal, result.Outcome, result.CompletedAtUtc,
            success ? result.Token.Ordinal : previous.LastSuccessfulOrdinal,
            success ? result.CompletedAtUtc : previous.LastSuccessfulAtUtc);
        var pause = result.Outcome == SourceRequestOutcome.AccessRestricted && result.Token.Ordinal > current.ResumeWatermark
            ? new SourceAccessPause(result.Token.Ordinal, result.ReceivedAtUtc ?? result.CompletedAtUtc, "access_restricted") : current.AccessPause;
        var cooldown = ExtendCooldown(current.Cooldown, result);
        return new(new(current.SourceId, checked(current.Revision + 1), progress, pause, cooldown, current.ResumeWatermark), true);
    }

    public static SourcePolicySnapshot Resume(SourcePolicySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current.AccessPause is null ? current : new(current.SourceId, checked(current.Revision + 1), current.Progress,
            null, current.Cooldown, current.Progress.LastIssuedOrdinal);
    }

    private static SourceCooldown? ExtendCooldown(SourceCooldown? previous, SourceRequestResultCommand result)
    {
        var receipt = result.ReceivedAtUtc ?? result.CompletedAtUtc;
        var delay = result.Outcome switch
        {
            SourceRequestOutcome.RateLimited => result.RetryAfter?.DelayAt(receipt) ?? TimeSpan.FromSeconds(60),
            SourceRequestOutcome.Maintenance => result.RetryAfter?.DelayAt(receipt) ?? TimeSpan.FromSeconds(120),
            SourceRequestOutcome.ServiceUnavailable => result.RetryAfter?.DelayAt(receipt),
            _ => null,
        };
        if (delay is null) return previous;
        var candidate = new SourceCooldown(receipt, delay.Value);
        return previous is not null && previous.NotBeforeUtc >= candidate.NotBeforeUtc ? previous : candidate;
    }
}

public sealed record SourcePolicyTransition
{
    internal SourcePolicyTransition(SourcePolicySnapshot policy, bool applied) { Policy = policy; Applied = applied; }
    public SourcePolicySnapshot Policy { get; }
    public bool Applied { get; }
}
