using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Migration;

/// <summary>
/// Explicit full-target choice. Production callers obtain that target from authoritative catalog results
/// after student review; this value object validates agreement, not the origin of a catalog assertion.
/// </summary>
public sealed record LegacyImportItem
{
    public LegacyImportItem(LegacyCourseIdentity source, SectionKey confirmedTarget, NewSubscription creation,
        LegacyHistoricalState? historicalState = null)
    {
        LegacyImportValidation.Target(source, confirmedTarget);
        ArgumentNullException.ThrowIfNull(creation);
        if (creation.Kind != SubscriptionKind.Section || creation.Sections.Length != 1 || creation.Sections[0] != confirmedTarget)
            throw new ArgumentException("An import creates one explicitly selected exact section.", nameof(creation));
        Source = source;
        ConfirmedTarget = confirmedTarget;
        Creation = creation;
        HistoricalState = historicalState;
    }

    public LegacyCourseIdentity Source { get; }
    public SectionKey ConfirmedTarget { get; }
    public NewSubscription Creation { get; }
    public LegacyHistoricalState? HistoricalState { get; }
}

public sealed record LegacyImportBatch
{
    public LegacyImportBatch(IEnumerable<LegacyImportItem> items)
    {
        Items = PersistenceValidation.Snapshot(items, PersistenceLimits.MaxSections, nameof(items));
        if (Items.IsEmpty) throw new ArgumentException("Select at least one legacy watch.", nameof(items));
        if (Items.Select(i => i.Source).Distinct().Count() != Items.Length
            || Items.Select(i => i.ConfirmedTarget).Distinct().Count() != Items.Length
            || Items.Select(i => i.Creation.Id).Distinct().Count() != Items.Length)
            throw new ArgumentException("Resolve duplicate or contradictory mappings before import.", nameof(items));
    }

    public ImmutableArray<LegacyImportItem> Items { get; }
}

public sealed record LegacyImportItemResult
{
    public LegacyImportItemResult(LegacyCourseIdentity source, SectionKey confirmedTarget, SubscriptionSnapshot subscription, bool created)
    {
        LegacyImportValidation.Target(source, confirmedTarget);
        ArgumentNullException.ThrowIfNull(subscription);
        if (subscription.Kind != SubscriptionKind.Section || subscription.Members.Length != 1 || subscription.Members[0].Key != confirmedTarget)
            throw new ArgumentException("The result must identify the selected destination section.", nameof(subscription));
        Source = source;
        ConfirmedTarget = confirmedTarget;
        Subscription = subscription;
        Created = created;
    }

    public LegacyCourseIdentity Source { get; }
    public SectionKey ConfirmedTarget { get; }
    public SubscriptionSnapshot Subscription { get; }
    public bool Created { get; }
}

public sealed record LegacyImportReceipt
{
    public LegacyImportReceipt(IEnumerable<LegacyImportItemResult> items)
    {
        Items = PersistenceValidation.Snapshot(items, PersistenceLimits.MaxSections, nameof(items));
        if (Items.Select(i => (i.Source, i.ConfirmedTarget)).Distinct().Count() != Items.Length)
            throw new ArgumentException("Import results must have unique ledger identities.", nameof(items));
    }

    public ImmutableArray<LegacyImportItemResult> Items { get; }
}

internal static class LegacyImportValidation
{
    internal static void Target(LegacyCourseIdentity source, SectionKey target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.Year != target.Year || source.CourseCode != target.CourseCode || source.Section != target.Section)
            throw new ArgumentException("A target must preserve legacy year, code and section; select its full term explicitly.", nameof(target));
    }
}
