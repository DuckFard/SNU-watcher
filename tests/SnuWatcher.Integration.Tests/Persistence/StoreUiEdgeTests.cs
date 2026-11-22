using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

/// <summary>Independent real-SQLite boundaries around accepted commands, read identities and bounded cached hints.</summary>
public sealed class StoreUiEdgeTests
{
    [Fact]
    public async Task AcceptedPreferenceIdentityRejectsChangedExpectedRevisionEvenWithIdenticalValues()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var preferences = (IUiPreferencesStore)fixture.Store;
        var id = Guid.NewGuid();
        var values = StoreUiPreferencesTests.Values();
        await preferences.TrySaveAsync(new(id, 0, values));

        await Assert.ThrowsAsync<UiPreferencesReplayConflictException>(() => preferences.TrySaveAsync(new(id, 1, values)));

        await fixture.ReopenAsync();
        var saved = await ((IUiPreferencesStore)fixture.Store).ReadAsync();
        Assert.Equal(1, saved.Revision);
        Assert.Equal(values, saved.Values);
        Assert.Equal(1L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    [Fact]
    public async Task WellFormedReceiptContradictingAcceptedValuesIsStorageCorruption()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var preferences = (IUiPreferencesStore)fixture.Store;
        var command = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, StoreUiPreferencesTests.Values());
        var saved = await preferences.TrySaveAsync(command);
        var receipt = JsonNode.Parse((string)StoreUiPreferencesTests.Scalar(fixture, "SELECT receipt FROM UiPreferenceWrites;")!)!;
        receipt["values"]!["language"] = "en";
        StoreUiPreferencesTests.Write(fixture, "UPDATE UiPreferenceWrites SET receipt=$receipt;", ("$receipt", receipt.ToJsonString()));

        await Assert.ThrowsAsync<InvalidDataException>(() => preferences.TrySaveAsync(command));

        Assert.Equal(saved.CurrentSnapshot, await preferences.ReadAsync());
        Assert.Equal(1L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM UiPreferenceWrites;"));
    }

    [Fact]
    public async Task LatestSourceEvidenceUsesCompletedOrdinalRatherThanLastResultArrivalOrUtc()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var policyStore = (ISourcePolicyStore)fixture.Store;
        var first = (await policyStore.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Detail), default)).Issued!;
        var second = (await policyStore.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Catalog), default)).Issued!;
        var secondStarted = fixture.Clock.GetUtcNow().AddSeconds(1);
        var secondCompleted = fixture.Clock.GetUtcNow().AddSeconds(2);
        await policyStore.RecordSourceRequestResultAsync(new(second.Token, SourceRequestOutcome.Success, secondCompleted,
            actualStartedAtUtc: secondStarted, receivedAtUtc: secondCompleted), default);
        var olderReceipt = await policyStore.RecordSourceRequestResultAsync(new(first.Token, SourceRequestOutcome.NetworkFailure,
            fixture.Clock.GetUtcNow().AddMinutes(1)), default);
        Assert.False(olderReceipt.PolicyApplied);

        await fixture.ReopenAsync();
        var status = await ((IWatchDashboardReader)fixture.Store).ReadSourceStatusAsync();

        Assert.Equal(second.Token.Ordinal, status.Policy.Progress.LatestCompletedOrdinal);
        Assert.Equal(SourceRequestOutcome.Success, status.Policy.Progress.LatestOutcome);
        Assert.Equal(second.Token.Ordinal, status.LatestCompletedTransport!.RequestOrdinal);
        Assert.Equal(secondStarted, status.LatestCompletedTransport.ActualStartedAtUtc);
        Assert.Equal(secondCompleted, status.LatestCompletedTransport.CompletedAtUtc);
    }

    [Fact]
    public async Task UnavailableHistoricalTransportEvidenceRemainsUnknownWithoutDiscardingSourceProgress()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var policyStore = (ISourcePolicyStore)fixture.Store;
        var request = (await policyStore.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Detail), default)).Issued!;
        await policyStore.RecordSourceRequestResultAsync(new(request.Token, SourceRequestOutcome.Success, fixture.Clock.GetUtcNow()), default);
        var originalPolicy = StoreUiPreferencesTests.Scalar(fixture, "SELECT snapshot FROM SourcePolicies;");
        StoreUiPreferencesTests.Write(fixture, "DELETE FROM SourceRequestResults WHERE requestId=$id;", ("$id", request.Token.Id.ToString("N")));

        var status = await ((IWatchDashboardReader)fixture.Store).ReadSourceStatusAsync();

        Assert.Null(status.LatestCompletedTransport);
        Assert.Equal(request.Token.Ordinal, status.Policy.Progress.LastSuccessfulOrdinal);
        Assert.Equal(originalPolicy, StoreUiPreferencesTests.Scalar(fixture, "SELECT snapshot FROM SourcePolicies;"));
        Assert.Equal(0L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM SourceRequestResults;"));
    }

    [Fact]
    public async Task SourceEvidenceForAnotherOrdinalFailsInsteadOfBeingDisplayedUnderCurrentPolicy()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var policyStore = (ISourcePolicyStore)fixture.Store;
        var request = (await policyStore.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Detail), default)).Issued!;
        await policyStore.RecordSourceRequestResultAsync(new(request.Token, SourceRequestOutcome.Unavailable, fixture.Clock.GetUtcNow()), default);
        var facts = JsonNode.Parse((string)StoreUiPreferencesTests.Scalar(fixture, "SELECT sourceFacts FROM SourceRequestResults;")!)!;
        facts["ordinal"] = request.Token.Ordinal + 1;
        var altered = facts.ToJsonString();
        StoreUiPreferencesTests.Write(fixture, "UPDATE SourceRequestResults SET sourceFacts=$facts;", ("$facts", altered));

        await Assert.ThrowsAsync<InvalidDataException>(() => ((IWatchDashboardReader)fixture.Store).ReadSourceStatusAsync());

        Assert.Equal(altered, StoreUiPreferencesTests.Scalar(fixture, "SELECT sourceFacts FROM SourceRequestResults;"));
    }

    [Fact]
    public async Task SummaryAndDetailsPreserveFullIdentityQuotedLabelAndResolvedExpiry()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = new SectionKey(2026, "U000200002", "U000300004", "V10.128", "001", "00");
        var label = "통계 '서울' \"College writing\"";
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 30));
        var created = await fixture.Store.CreateSubscriptionAsync(fixture.NewSection(key, false, label, expiry));
        var paused = await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(created.Id, created.DefinitionRevision, fixture.Clock.GetUtcNow()));
        await fixture.ReopenAsync();

        var reader = (IWatchDashboardReader)fixture.Store;
        var summary = Assert.Single((await reader.ReadWatchPageAsync()).Page!.Items);
        var details = Assert.IsType<WatchDetailsSnapshot>(await reader.ReadWatchDetailsAsync(created.Id));

        Assert.Equal(label, summary.Label);
        Assert.Equal(paused.DefinitionRevision, summary.DefinitionRevision);
        Assert.Equal(WatchStatus.Paused, summary.StoredStatus);
        Assert.Equal(expiry, summary.Expiry);
        Assert.Equal("U000300004", summary.Term.DetailSemesterCode);
        Assert.Equal(key, summary.SingleSection!.Key);
        Assert.Equal(key, Assert.Single(details.Sections).Key);
        Assert.Equal(expiry, details.Subscription.Expiry);
        Assert.Equal("001", summary.SingleSection.Key.Section);
        Assert.Equal("00", summary.SingleSection.Key.SubsubjectCode);
    }

    [Fact]
    public async Task MaximumPageUsesAllFiftyRowsAndItsExclusiveCursorWithoutDroppingTheFiftyFirst()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        for (var index = 1; index <= 51; index++)
            await fixture.Store.CreateSubscriptionAsync(new(Id(index), "Watch " + index, SubscriptionKind.Section,
                [key], false, null, fixture.Clock.GetUtcNow()));
        var reader = (IWatchDashboardReader)fixture.Store;

        var first = (await reader.ReadWatchPageAsync(pageSize: 50)).Page!;
        var last = (await reader.ReadWatchPageAsync(first.NextCursor, 50)).Page!;

        Assert.Equal(Enumerable.Range(1, 50).Select(Id), first.Items.Select(watch => watch.Id));
        Assert.True(first.HasMore);
        Assert.Equal(Id(50), first.NextCursor!.AfterSubscriptionId);
        Assert.Equal(Id(51), Assert.Single(last.Items).Id);
        Assert.False(last.HasMore);
        Assert.Null(last.NextCursor);
        Assert.Equal(first.Stamp.CatalogRevision, last.Stamp.CatalogRevision);
    }

    [Fact]
    public async Task GlobalCacheRevisionOverflowLeavesTheCurrentYearAndOrderUnchanged()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var cache = (ITermHintCache)fixture.Store;
        await cache.TryReplaceYearAsync(TermsCommand(0, 2026, fixture.Clock.GetUtcNow()));
        StoreUiPreferencesTests.Write(fixture, "UPDATE UiTermCacheState SET revision=9223372036854775807; UPDATE UiTermHints SET cacheRevision=9223372036854775807;");
        var original = (string)StoreUiPreferencesTests.Scalar(fixture, "SELECT hintsJson FROM UiTermHints;")!;

        await Assert.ThrowsAsync<OverflowException>(() => cache.TryReplaceYearAsync(TermsCommand(long.MaxValue, 2025, fixture.Clock.GetUtcNow())));

        await fixture.ReopenAsync();
        var saved = await ((ITermHintCache)fixture.Store).ReadAsync();
        Assert.Equal(long.MaxValue, saved.Revision);
        Assert.Equal(2026, Assert.Single(saved.Years).Year);
        Assert.Equal(long.MaxValue, Assert.Single(saved.Years).CacheRevision);
        Assert.Equal(original, StoreUiPreferencesTests.Scalar(fixture, "SELECT hintsJson FROM UiTermHints;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateOrOversizedStoredTermsFailWithoutTruncationOrReplacement(bool oversized)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var cache = (ITermHintCache)fixture.Store;
        await cache.TryReplaceYearAsync(TermsCommand(0, 2026, fixture.Clock.GetUtcNow()));
        var json = oversized
            ? JsonSerializer.Serialize(new { version = 1, terms = new[] { new { selector = "Term.1", name = new string('x', 1024 * 1024), english = (string?)null } } })
            : "{\"version\":1,\"terms\":[{\"selector\":\"Term.1\",\"name\":\"First\",\"english\":null},{\"selector\":\"Term.1\",\"name\":\"Second\",\"english\":null}]}";
        StoreUiPreferencesTests.Write(fixture, "UPDATE UiTermHints SET hintsJson=$json;", ("$json", json));

        if (oversized)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ReadAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.TryReplaceYearAsync(TermsCommand(1, 2026, fixture.Clock.GetUtcNow())));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => cache.ReadAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => cache.TryReplaceYearAsync(TermsCommand(1, 2026, fixture.Clock.GetUtcNow())));
        }
        Assert.Equal(json, StoreUiPreferencesTests.Scalar(fixture, "SELECT hintsJson FROM UiTermHints;"));
        Assert.Equal(1L, StoreUiPreferencesTests.Scalar(fixture, "SELECT revision FROM UiTermCacheState;"));
    }

    private static Guid Id(int value) => Guid.ParseExact(value.ToString("x32", CultureInfo.InvariantCulture), "N");
    private static ReplaceTermHintsCommand TermsCommand(long revision, int year, DateTimeOffset at) =>
        new(revision, year, at, [new CatalogTerm(year, "Term.1", "학기", "Term")]);
}
