using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Migration;

/// <summary>
/// An explicit choice among full catalog identities. The caller supplies actual catalog results after
/// student review; this pure model cannot attest their remote origin and accepts no verification boolean.
/// </summary>
public sealed record LegacyImportSelection
{
    public LegacyImportSelection(LegacyCourseIdentity source, IEnumerable<SectionKey> catalogCandidates,
        SectionKey selectedTarget, NewSubscription creation)
    {
        var item = new LegacyImportItem(source, selectedTarget, creation);
        CatalogCandidates = PersistenceValidation.Snapshot(catalogCandidates, PersistenceLimits.MaxSections, nameof(catalogCandidates))
            .Distinct().ToImmutableArray();
        if (!CatalogCandidates.Contains(selectedTarget))
            throw new ArgumentException("Explicitly select one exact identity from the catalog candidates.", nameof(selectedTarget));
        Source = item.Source;
        SelectedTarget = item.ConfirmedTarget;
        Creation = item.Creation;
    }

    public LegacyCourseIdentity Source { get; }
    public ImmutableArray<SectionKey> CatalogCandidates { get; }
    public SectionKey SelectedTarget { get; }
    public NewSubscription Creation { get; }
}

/// <summary>Builds a confirmed batch without I/O, implicit current terms or automatic row selection.</summary>
public static class LegacyImportConfirmation
{
    /// <summary>
    /// Validates explicit caller-reviewed catalog choices and copies provenance only from matching preview rows.
    /// It does not contact a catalog or reserve a seat; authoritative catalog lookup remains the caller boundary.
    /// </summary>
    public static LegacyImportBatch Confirm(LegacyReadSnapshot snapshot, IEnumerable<LegacyImportSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var choices = PersistenceValidation.Snapshot(selections, PersistenceLimits.MaxSections, nameof(selections));
        var definitions = snapshot.Definitions.ToDictionary(d => d.Identity);
        var items = choices.Select(choice =>
        {
            if (!definitions.TryGetValue(choice.Source, out var definition))
                throw new ArgumentException("The selected legacy identity is absent from this preview.", nameof(selections));
            return new LegacyImportItem(choice.Source, choice.SelectedTarget, choice.Creation, definition.HistoricalState);
        });
        return new LegacyImportBatch(items);
    }
}
