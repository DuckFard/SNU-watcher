using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

public enum SubscriptionKind { Section, Group }
public enum WatchStatus { Active, Paused, Expired, Removed }

public sealed record NewSubscription
{
    public NewSubscription(Guid id, string label, SubscriptionKind kind, IEnumerable<SectionKey> sections,
        bool initialNoticeEnabled, WatchExpiry? expiry, DateTimeOffset createdAtUtc)
    {
        Id = PersistenceValidation.Id(id, nameof(id));
        Label = PersistenceValidation.Label(label);
        Kind = PersistenceValidation.Defined(kind, nameof(kind));
        Sections = PersistenceValidation.Sections(sections);
        if (kind == SubscriptionKind.Section && Sections.Length != 1)
            throw new ArgumentException("A section watch has exactly one member.", nameof(sections));
        InitialNoticeEnabled = initialNoticeEnabled;
        Expiry = PersistenceValidation.Expiry(expiry);
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public Guid Id { get; }
    public string Label { get; }
    public SubscriptionKind Kind { get; }
    public ImmutableArray<SectionKey> Sections { get; }
    public bool InitialNoticeEnabled { get; }
    public WatchExpiry? Expiry { get; }
    public DateTimeOffset CreatedAtUtc { get; }
}

/// <summary>Initialization is scoped by the containing subscription and its activation generation.</summary>
public sealed record MemberSnapshot
{
    public MemberSnapshot(SectionKey key, long activationWatermark, long? initializedByAttemptOrdinal)
    {
        ArgumentNullException.ThrowIfNull(key);
        ActivationWatermark = PersistenceValidation.Nonnegative(activationWatermark, nameof(activationWatermark));
        if (initializedByAttemptOrdinal is { } ordinal && ordinal <= activationWatermark)
            throw new ArgumentException("Initialization requires an attempt issued after activation.", nameof(initializedByAttemptOrdinal));
        Key = key;
        InitializedByAttemptOrdinal = initializedByAttemptOrdinal;
    }

    public SectionKey Key { get; }
    public long ActivationWatermark { get; }
    public long? InitializedByAttemptOrdinal { get; }
}

public sealed record SubscriptionSnapshot
{
    public SubscriptionSnapshot(Guid id, long definitionRevision, long activationGeneration, SubscriptionKind kind,
        string label, WatchStatus status, bool initialNoticeEnabled, WatchExpiry? expiry,
        DateTimeOffset createdAtUtc, IEnumerable<MemberSnapshot> members)
    {
        Id = PersistenceValidation.Id(id, nameof(id));
        DefinitionRevision = PersistenceValidation.Positive(definitionRevision, nameof(definitionRevision));
        ActivationGeneration = PersistenceValidation.Positive(activationGeneration, nameof(activationGeneration));
        Kind = PersistenceValidation.Defined(kind, nameof(kind));
        Label = PersistenceValidation.Label(label);
        Status = PersistenceValidation.Defined(status, nameof(status));
        InitialNoticeEnabled = initialNoticeEnabled;
        Expiry = PersistenceValidation.Expiry(expiry);
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Members = PersistenceValidation.Snapshot(members, PersistenceLimits.MaxSections, nameof(members));
        var keys = PersistenceValidation.Sections(Members.Select(m => m.Key));
        if (keys.Length != Members.Length || (kind == SubscriptionKind.Section && Members.Length != 1))
            throw new ArgumentException("Membership must be unique and match the subscription kind.", nameof(members));
    }

    public Guid Id { get; }
    public long DefinitionRevision { get; }
    public long ActivationGeneration { get; }
    public SubscriptionKind Kind { get; }
    public string Label { get; }
    public WatchStatus Status { get; }
    public bool InitialNoticeEnabled { get; }
    public WatchExpiry? Expiry { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public ImmutableArray<MemberSnapshot> Members { get; }
}
