using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Presentation.Reads;

/// <summary>Collapsed group summaries deliberately contain no eager member-state or health aggregate.</summary>
public sealed record WatchDefinitionSummary
{
    public WatchDefinitionSummary(Guid id, long definitionRevision, long activationGeneration, SubscriptionKind kind,
        string label, WatchStatus storedStatus, bool initialNoticeEnabled, WatchExpiry? expiry,
        DateTimeOffset createdAtUtc, AcademicTermIdentity term, int memberCount, SectionSnapshot? singleSection)
    {
        Id = PersistenceValidation.Id(id, nameof(id));
        DefinitionRevision = PersistenceValidation.Positive(definitionRevision, nameof(definitionRevision));
        ActivationGeneration = PersistenceValidation.Positive(activationGeneration, nameof(activationGeneration));
        Kind = PersistenceValidation.Defined(kind, nameof(kind));
        Label = PersistenceValidation.Label(label);
        StoredStatus = PersistenceValidation.Defined(storedStatus, nameof(storedStatus));
        if (storedStatus == WatchStatus.Removed) throw new ArgumentException("Removed watches are not presentation results.", nameof(storedStatus));
        ArgumentNullException.ThrowIfNull(term);
        if (memberCount is < 1 or > PersistenceLimits.MaxSections) throw new ArgumentOutOfRangeException(nameof(memberCount));
        if (kind == SubscriptionKind.Section && (memberCount != 1 || singleSection is null || new AcademicTermIdentity(singleSection.Key) != term)
            || kind == SubscriptionKind.Group && singleSection is not null)
            throw new ArgumentException("Section watches need their one exact state; group state is loaded through details.", nameof(singleSection));
        InitialNoticeEnabled = initialNoticeEnabled; Expiry = PersistenceValidation.Expiry(expiry);
        CreatedAtUtc = createdAtUtc.ToUniversalTime(); Term = term; MemberCount = memberCount; SingleSection = singleSection;
    }
    public Guid Id { get; }
    public long DefinitionRevision { get; }
    public long ActivationGeneration { get; }
    public SubscriptionKind Kind { get; }
    public string Label { get; }
    public WatchStatus StoredStatus { get; }
    public bool InitialNoticeEnabled { get; }
    public WatchExpiry? Expiry { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public AcademicTermIdentity Term { get; }
    public int MemberCount { get; }
    public SectionSnapshot? SingleSection { get; }
}

public sealed record WatchPageSnapshot
{
    public const int MaximumPageSize = 50;
    public WatchPageSnapshot(UiReadStamp stamp, IEnumerable<WatchDefinitionSummary> items, bool hasMore, WatchPageCursor? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        Items = PersistenceValidation.Snapshot(items, MaximumPageSize, nameof(items));
        for (var index = 1; index < Items.Length; index++)
            if (string.CompareOrdinal(Items[index - 1].Id.ToString("N"), Items[index].Id.ToString("N")) >= 0)
                throw new ArgumentException("Page identities must be unique and ascending in canonical Guid-N order.", nameof(items));
        if (hasMore != (nextCursor is not null) || hasMore && (Items.IsEmpty || nextCursor!.CatalogRevision != stamp.CatalogRevision
            || nextCursor.AfterSubscriptionId != Items[^1].Id))
            throw new ArgumentException("A continuing page requires its exact last identity and catalog epoch.", nameof(nextCursor));
        Stamp = stamp; HasMore = hasMore; NextCursor = nextCursor;
    }
    public UiReadStamp Stamp { get; }
    public ImmutableArray<WatchDefinitionSummary> Items { get; }
    public bool HasMore { get; }
    public WatchPageCursor? NextCursor { get; }
}

public enum WatchPageReadDisposition { Page, RestartRequired }

public sealed record WatchPageReadResult
{
    public WatchPageReadResult(WatchPageReadDisposition disposition, long currentCatalogRevision, WatchPageSnapshot? page)
    {
        Disposition = PersistenceValidation.Defined(disposition, nameof(disposition));
        CurrentCatalogRevision = PersistenceValidation.Nonnegative(currentCatalogRevision, nameof(currentCatalogRevision));
        if (disposition == WatchPageReadDisposition.Page && (page is null || page.Stamp.CatalogRevision != currentCatalogRevision)
            || disposition == WatchPageReadDisposition.RestartRequired && page is not null)
            throw new ArgumentException("A result is either a matching epoch page or a restart without items.", nameof(page));
        Page = page;
    }
    public WatchPageReadDisposition Disposition { get; }
    public long CurrentCatalogRevision { get; }
    public WatchPageSnapshot? Page { get; }
}

/// <summary>Atomic exact current membership. The reader supplies Unchecked for a member with no persisted observation.</summary>
public sealed record WatchDetailsSnapshot
{
    public WatchDetailsSnapshot(UiReadStamp stamp, SubscriptionSnapshot subscription, IEnumerable<SectionSnapshot> sections)
    {
        ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(subscription);
        Sections = PersistenceValidation.Snapshot(sections, PersistenceLimits.MaxSections, nameof(sections));
        if (subscription.Status == WatchStatus.Removed || Sections.Length != subscription.Members.Length
            || Sections.DistinctBy(section => section.Key).Count() != Sections.Length
            || !Sections.Select(section => section.Key).ToImmutableHashSet().SetEquals(subscription.Members.Select(member => member.Key)))
            throw new ArgumentException("Details require exactly the nonremoved watch's current member states.", nameof(sections));
        Stamp = stamp; Subscription = subscription;
    }
    public UiReadStamp Stamp { get; }
    public SubscriptionSnapshot Subscription { get; }
    public ImmutableArray<SectionSnapshot> Sections { get; }
}

/// <summary>Latest completed physical request facts. Nullable start/receipt and clock changes remain honest.</summary>
public sealed record CompletedTransportEvidence
{
    public CompletedTransportEvidence(long requestOrdinal, DateTimeOffset? actualStartedAtUtc,
        DateTimeOffset? receivedAtUtc, DateTimeOffset completedAtUtc)
    {
        RequestOrdinal = PersistenceValidation.Positive(requestOrdinal, nameof(requestOrdinal));
        ActualStartedAtUtc = actualStartedAtUtc?.ToUniversalTime(); ReceivedAtUtc = receivedAtUtc?.ToUniversalTime();
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
    }
    public long RequestOrdinal { get; }
    public DateTimeOffset? ActualStartedAtUtc { get; }
    public DateTimeOffset? ReceivedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
}

public sealed record SourceStatusSnapshot
{
    public SourceStatusSnapshot(UiReadStamp stamp, SourcePolicySnapshot policy, CompletedTransportEvidence? latestCompletedTransport)
    {
        ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(policy);
        if (latestCompletedTransport is { } evidence && (evidence.RequestOrdinal != policy.Progress.LatestCompletedOrdinal
            || evidence.CompletedAtUtc != policy.Progress.LatestCompletedAtUtc))
            throw new ArgumentException("Transport evidence must identify the latest completed physical request.", nameof(latestCompletedTransport));
        Stamp = stamp; Policy = policy; LatestCompletedTransport = latestCompletedTransport;
    }
    public UiReadStamp Stamp { get; }
    public SourcePolicySnapshot Policy { get; }
    public CompletedTransportEvidence? LatestCompletedTransport { get; }
}
