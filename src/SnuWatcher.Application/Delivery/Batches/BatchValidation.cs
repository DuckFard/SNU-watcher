using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Delivery.Batches;

/// <summary>Bounds materialized work and recipient summaries, never the relational event history.</summary>
public static class BatchDeliveryLimits
{
    public const int MaxPageSize = PersistenceLimits.MaxPendingRead;
    public const int MaxRecipients = PersistenceLimits.MaxSections;
}

internal static class BatchValidation
{
    internal static string Digest(string digest) => digest is { Length: 64 }
        && digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? digest
        : throw new ArgumentException("Coverage uses a canonical lowercase SHA-256 digest.", nameof(digest));

    internal static string Code(string code) => code is { Length: > 0 and <= 64 }
        && code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-') ? code
        : throw new ArgumentException("Diagnostics use a bounded machine code, never raw text.", nameof(code));

    internal static Guid? OptionalId(Guid? id, string name) => id is null ? null : PersistenceValidation.Id(id.Value, name);

    internal static ImmutableArray<BatchRecipient> Recipients(IEnumerable<BatchRecipient> recipients)
    {
        var snapshot = PersistenceValidation.Snapshot(recipients, BatchDeliveryLimits.MaxRecipients, nameof(recipients));
        if (snapshot.DistinctBy(r => (r.SubscriptionId, r.Generation)).Count() != snapshot.Length)
            throw new ArgumentException("Recipient generations must be unique.", nameof(recipients));
        return snapshot.OrderBy(r => r.SubscriptionId.ToString("N"), StringComparer.Ordinal).ThenBy(r => r.Generation).ToImmutableArray();
    }

    internal static bool SameRecipients(ImmutableArray<BatchRecipient> first, ImmutableArray<BatchRecipient> second) =>
        first.Length == second.Length && first.Zip(second).All(pair => pair.First.SubscriptionId == pair.Second.SubscriptionId
            && pair.First.Generation == pair.Second.Generation && pair.First.Reasons.SequenceEqual(pair.Second.Reasons));
}
