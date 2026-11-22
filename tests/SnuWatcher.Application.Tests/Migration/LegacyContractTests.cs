using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Migration;

public sealed class LegacyContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
    private static LegacyCourseIdentity Source(string section = "012") => new(2026, "U000200002", "F32.102", section);
    private static SectionKey Target(string semester = "2", string detail = "U000200002", string subsubject = "000") =>
        new(2026, semester, detail, "F32.102", "012", subsubject);
    private static NewSubscription Creation(SectionKey target, string label = "Statistics") =>
        new(Guid.NewGuid(), label, SubscriptionKind.Section, [target], false, null, Now);

    [Fact]
    public void FourPartIdentityPreservesPunctuationLeadingZerosAndStableFormatFamily()
    {
        var identity = Source();
        Assert.Equal("2026:U000200002:F32.102:012", identity.StorageKey);
        Assert.Equal("python-watcher-v0:" + identity.StorageKey, identity.CanonicalKey);
        Assert.Equal(identity, Source());
        Assert.Equal(2000, new LegacyCourseIdentity(2000, "U000200002", "4190.412", "001").Year);
        Assert.Throws<ArgumentException>(() => new LegacyCourseIdentity(2026, "2", "F32.102", "012"));
        Assert.Throws<ArgumentException>(() => new LegacyCourseIdentity(2026, "U000200002", "F32.102", "٠١٢"));
        Assert.Throws<ArgumentException>(() => new LegacyCourseIdentity(2026, "U000200002", "F32.102", "012\n"));
    }

    [Fact]
    public void ReadSnapshotIsImmutableAndKeepsHistoricalOnlyRowsDistinctFromConfiguredWatches()
    {
        var old = new LegacyHistoricalState("통계학", 116, 120, LegacyAvailability.Available, "2026-08-28T10:00:00", null);
        var definitions = new List<LegacyDefinition> { new(Source(), "Statistics", false, old) };
        var diagnostics = new List<LegacyDiagnostic> { new(LegacySourceKind.StateDatabase, "historical-only", 1) };
        var snapshot = new LegacyReadSnapshot(definitions, diagnostics);
        definitions.Clear();
        diagnostics.Clear();
        var definition = Assert.Single(snapshot.Definitions);
        Assert.False(definition.WasConfigured);
        Assert.Null(definition.HistoricalState!.ObservedAtUtc);
        Assert.Equal(116, definition.HistoricalState.Registered);
        Assert.Single(snapshot.Diagnostics);
        Assert.Throws<NotSupportedException>(() => ((IList<LegacyDefinition>)snapshot.Definitions).Clear());
    }

    [Fact]
    public void SourceSelectionRequiresAnExplicitSourceWithoutOpeningOrResolvingPaths()
    {
        Assert.Throws<ArgumentException>(() => new LegacySourceSelection());
        Assert.Throws<ArgumentException>(() => new LegacySourceSelection(" "));
        var selection = new LegacySourceSelection("chosen.toml", "chosen.sqlite3");
        Assert.Equal("chosen.toml", selection.ConfigPath);
        Assert.Equal("chosen.sqlite3", selection.StatePath);
    }

    [Fact]
    public void ImportRequiresExplicitFullTargetAndMatchingSingleSectionCreation()
    {
        var target = Target("S", "SUMMER", "003");
        var item = new LegacyImportItem(Source(), target, Creation(target));
        Assert.Equal("SUMMER", item.ConfirmedTarget.DetailSemesterCode);
        Assert.Equal("003", item.ConfirmedTarget.SubsubjectCode);
        Assert.False(item.Creation.InitialNoticeEnabled);
        var other = new SectionKey(2026, "2", "U000200002", "F33.107", "012", "000");
        Assert.Throws<ArgumentException>(() => new LegacyImportItem(Source(), other, Creation(other)));
        Assert.Throws<ArgumentException>(() => new LegacyImportItem(Source(), target, Creation(Target())));
        Assert.Throws<ArgumentException>(() => new LegacyImportItem(Source(), target,
            new(Guid.NewGuid(), "Group", SubscriptionKind.Group, [target], true, null, Now)));
    }

    [Fact]
    public void BatchRejectsDuplicateAndContradictoryMappingsRatherThanLastWinning()
    {
        var first = new LegacyImportItem(Source(), Target(), Creation(Target()));
        Assert.Throws<ArgumentException>(() => new LegacyImportBatch([]));
        Assert.Throws<ArgumentException>(() => new LegacyImportBatch([first, first]));
        var contradictory = new LegacyImportItem(Source(), Target(subsubject: "001"), Creation(Target(subsubject: "001")));
        Assert.Throws<ArgumentException>(() => new LegacyImportBatch([first, contradictory]));
    }

    [Fact]
    public void PreviewDuplicateIdentitiesAndUnsanitizedDiagnosticsAreRejected()
    {
        var definition = new LegacyDefinition(Source(), "Statistics", true);
        Assert.Throws<ArgumentException>(() => new LegacyReadSnapshot([definition, definition], []));
        Assert.Throws<ArgumentException>(() => new LegacyDiagnostic(LegacySourceKind.Configuration, "raw URL or private path"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LegacyDiagnostic(LegacySourceKind.Configuration, "bad-field", lineNumber: 0));
        var diagnostic = new LegacyDiagnostic(LegacySourceKind.Configuration, "bad-field", lineNumber: 2, columnNumber: 3);
        Assert.Equal(2, diagnostic.LineNumber);
        Assert.Equal(3, diagnostic.ColumnNumber);
    }

    [Fact]
    public void ReceiptPreservesPausedExistingWatchWithoutResumingOrReplacingPreferences()
    {
        var target = Target();
        var snapshot = new SubscriptionSnapshot(Guid.NewGuid(), 4, 2, SubscriptionKind.Section, "Student label", WatchStatus.Paused,
            true, null, Now, [new(target, 3, 4)]);
        var results = new List<LegacyImportItemResult> { new(Source(), target, snapshot, false) };
        var receipt = new LegacyImportReceipt(results);
        results.Clear();
        var result = Assert.Single(receipt.Items);
        Assert.False(result.Created);
        Assert.Equal(WatchStatus.Paused, result.Subscription.Status);
        Assert.Equal(4, result.Subscription.Members[0].InitializedByAttemptOrdinal);
        Assert.Equal("Student label", result.Subscription.Label);
    }
}
