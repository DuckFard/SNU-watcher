namespace SnuWatcher.Infrastructure.Snu.Policy;

internal sealed class SourcePolicyStorageException() : IOException("Saved source policy is unavailable. Recover source policy before another request.");

/// <summary>Local no-send control flow. It is mapped for callers only after leaving the dispatcher's callback.</summary>
internal sealed class SourcePolicyBlockedException(SourceFailureKind kind, TimeSpan? retryAfter) : IOException("Saved source policy currently blocks this request.")
{
    public SourceFailureKind Kind { get; } = kind;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
