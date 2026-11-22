using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Migration;

public sealed class LegacyConfirmationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
    private static LegacyCourseIdentity Source(string section = "012") => new(2026, "U000200002", "F32.102", section);
    private static SectionKey Target(string subsubject = "000") => new(2026, "2", "U000200002", "F32.102", "012", subsubject);
    private static NewSubscription Creation(SectionKey target) => new(Guid.NewGuid(), "Chosen label", SubscriptionKind.Section,
        [target], false, WatchExpiry.FromLocalDate(new DateOnly(2026, 12, 1)), Now);

    [Fact]
    public void AmbiguousCatalogCandidatesNeedAnExplicitSelectedFullIdentity()
    {
        var candidates = new List<SectionKey> { Target(), Target("001") };
        var creation = Creation(candidates[1]);
        var selection = new LegacyImportSelection(Source(), candidates, candidates[1], creation);
        candidates.Clear();
        var preview = new LegacyReadSnapshot([new(Source(), "Old label", true)], []);
        var batch = LegacyImportConfirmation.Confirm(preview, [selection]);

        var item = Assert.Single(batch.Items);
        Assert.Equal("001", item.ConfirmedTarget.SubsubjectCode);
        Assert.Equal(2, selection.CatalogCandidates.Length);
        Assert.Same(creation, item.Creation);
        Assert.False(item.Creation.InitialNoticeEnabled);
        Assert.Equal(creation.Expiry, item.Creation.Expiry);
    }

    [Fact]
    public void SelectionMustExistInTheProvidedExactCatalogCandidates()
    {
        Assert.Throws<ArgumentException>(() => new LegacyImportSelection(Source(), [], Target(), Creation(Target())));
        Assert.Throws<ArgumentException>(() => new LegacyImportSelection(Source(), [Target()], Target("001"), Creation(Target("001"))));
    }

    [Fact]
    public void ConfirmationCopiesOnlyMatchingHistoricalProvenanceAndDoesNotSelectOtherRows()
    {
        var historical = new LegacyHistoricalState("Old course", 116, 120, LegacyAvailability.Available,
            "2026-08-28T10:00:00+09:00", new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero));
        var preview = new LegacyReadSnapshot([new(Source(), "Historical only", false, historical),
            new(Source("013"), "Configured but not selected", true)], []);
        var selection = new LegacyImportSelection(Source(), [Target()], Target(), Creation(Target()));

        var item = Assert.Single(LegacyImportConfirmation.Confirm(preview, [selection]).Items);

        Assert.Same(historical, item.HistoricalState);
        Assert.Equal("Chosen label", item.Creation.Label);
        Assert.Equal(Source(), item.Source);
    }

    [Fact]
    public void UnknownPreviewSourceAndEmptySelectionCannotBeConfirmed()
    {
        var preview = new LegacyReadSnapshot([], []);
        var selection = new LegacyImportSelection(Source(), [Target()], Target(), Creation(Target()));
        Assert.Throws<ArgumentException>(() => LegacyImportConfirmation.Confirm(preview, [selection]));
        Assert.Throws<ArgumentException>(() => LegacyImportConfirmation.Confirm(preview, []));
    }

    [Fact]
    public void NaiveHistoricalTimesCannotBeSilentlyAssignedTheHostTimezone()
    {
        Assert.Throws<ArgumentException>(() => new LegacyHistoricalState("Old", 1, 2, LegacyAvailability.Available,
            "2026-08-28T10:00:00", Now));
        var unknownZone = new LegacyHistoricalState("Old", 1, 2, LegacyAvailability.Available, "2026-08-28T10:00:00", null);
        Assert.Null(unknownZone.ObservedAtUtc);
        Assert.Equal("2026-08-28T10:00:00", unknownZone.OriginalObservedText);
    }
}
