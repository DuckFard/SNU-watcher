namespace SnuWatcher.Application.Monitoring.SourcePolicy;

/// <summary>Narrow source-wide restrictions and ordered transport evidence; never section baseline setters.</summary>
public interface ISourcePolicyStore
{
    Task<SourcePolicySnapshot> ReadSourcePolicyAsync(string sourceId, CancellationToken cancellationToken);
    Task<SourceRequestIssueResult> TryIssueSourceRequestAsync(SourceRequestIssue command, CancellationToken cancellationToken);
    Task<SourcePolicyReceipt> RecordSourceRequestResultAsync(SourceRequestResultCommand command, CancellationToken cancellationToken);
    Task<SourceAccessResumeResult> ResumeSourceAccessAsync(SourceAccessResumeCommand command, CancellationToken cancellationToken);
}

public sealed record SourceRequestIssueResult
{
    public SourceRequestIssueResult(SourcePolicySnapshot policy, IssuedSourceRequest? issued)
    {
        ArgumentNullException.ThrowIfNull(policy); Policy = policy; Issued = issued;
        if (issued is not null && (issued.Token.SourceId != policy.SourceId
            || issued.Token.Ordinal != policy.Progress.LastIssuedOrdinal))
            throw new ArgumentException("Issued intent must match the policy snapshot.", nameof(issued));
    }
    public SourcePolicySnapshot Policy { get; }
    public IssuedSourceRequest? Issued { get; }
    public bool WasIssued => Issued is not null;
}

public sealed record SourcePolicyReceipt
{
    public SourcePolicyReceipt(SourceRequestToken token, bool policyApplied, long policyRevision)
    {
        ArgumentNullException.ThrowIfNull(token); Token = token;
        if (policyRevision < 0) throw new ArgumentOutOfRangeException(nameof(policyRevision));
        PolicyApplied = policyApplied; PolicyRevision = policyRevision;
    }
    public SourceRequestToken Token { get; }
    public bool PolicyApplied { get; }
    public long PolicyRevision { get; }
}

public sealed record SourceAccessResumeResult
{
    public SourceAccessResumeResult(bool applied, SourcePolicySnapshot policy)
    { ArgumentNullException.ThrowIfNull(policy); Applied = applied; Policy = policy; }
    public bool Applied { get; }
    public SourcePolicySnapshot Policy { get; }
}

public sealed class SourceRequestReplayConflictException : InvalidOperationException
{
    public SourceRequestReplayConflictException() : base("The source request already has different immutable result facts.") { }
}
