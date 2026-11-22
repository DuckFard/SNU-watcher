using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

/// <summary>Closed command family. Store checks revision and updates catalog revision transactionally.</summary>
public abstract record SubscriptionChange
{
    private protected SubscriptionChange(Guid id, long expectedRevision, DateTimeOffset changedAtUtc)
    {
        Id = PersistenceValidation.Id(id, nameof(id));
        ExpectedRevision = PersistenceValidation.Positive(expectedRevision, nameof(expectedRevision));
        ChangedAtUtc = changedAtUtc.ToUniversalTime();
    }

    public Guid Id { get; }
    public long ExpectedRevision { get; }
    public DateTimeOffset ChangedAtUtc { get; }
}

public sealed record PauseSubscription : SubscriptionChange
{
    public PauseSubscription(Guid id, long expectedRevision, DateTimeOffset changedAtUtc)
        : base(id, expectedRevision, changedAtUtc) { }
}

public sealed record ResumeSubscription : SubscriptionChange
{
    public ResumeSubscription(Guid id, long expectedRevision, DateTimeOffset changedAtUtc)
        : base(id, expectedRevision, changedAtUtc) { }
}

public sealed record RemoveSubscription : SubscriptionChange
{
    public RemoveSubscription(Guid id, long expectedRevision, DateTimeOffset changedAtUtc)
        : base(id, expectedRevision, changedAtUtc) { }
}

public sealed record RenameSubscription : SubscriptionChange
{
    public RenameSubscription(Guid id, long expectedRevision, DateTimeOffset changedAtUtc, string label)
        : base(id, expectedRevision, changedAtUtc) => Label = PersistenceValidation.Label(label);

    public string Label { get; }
}

public sealed record SetSubscriptionExpiry : SubscriptionChange
{
    public SetSubscriptionExpiry(Guid id, long expectedRevision, DateTimeOffset changedAtUtc, WatchExpiry? expiry)
        : base(id, expectedRevision, changedAtUtc) => Expiry = PersistenceValidation.Expiry(expiry);

    public WatchExpiry? Expiry { get; }
}
