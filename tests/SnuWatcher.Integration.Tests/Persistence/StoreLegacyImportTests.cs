using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreLegacyImportTests
{
    [Fact]
    public async Task HistoricalCountsStayProvenanceAndRepeatImportKeepsTheOriginalAssociation()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = Item(fixture, "012", historyRegistered: 100);
        var receipt = await fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([first]), default);
        var result = Assert.Single(receipt.Items);
        Assert.True(result.Created);
        Assert.Null(Assert.Single(result.Subscription.Members).InitializedByAttemptOrdinal);
        Assert.Null((await fixture.Store.ReadSectionAsync(first.ConfirmedTarget, default)).Baseline);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));

        await fixture.ReopenAsync();
        var changed = Item(fixture, "012", historyRegistered: 95, label: "Changed old file name and counts");
        var replay = Assert.Single((await fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([changed]), default)).Items);
        Assert.False(replay.Created);
        Assert.Equal(first.Creation.Id, replay.Subscription.Id);
        Assert.Equal(first.Creation.Label, replay.Subscription.Label);
        Assert.Single(await fixture.Store.ListSubscriptionsAsync(default));
        using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open(SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provenance FROM LegacyImports;";
        using var provenance = JsonDocument.Parse((string)command.ExecuteScalar()!);
        Assert.Equal(100, provenance.RootElement.GetProperty("registered").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReimportDoesNotRenameResumeOrResurrectExistingWatches(bool remove)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = Item(fixture, "012");
        var subscription = Assert.Single((await fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([first]), default)).Items).Subscription;
        subscription = await fixture.Store.ChangeSubscriptionAsync(new RenameSubscription(subscription.Id,
            subscription.DefinitionRevision, fixture.Clock.GetUtcNow(), "Student's new name"), default);
        subscription = await fixture.Store.ChangeSubscriptionAsync(remove
            ? new RemoveSubscription(subscription.Id, subscription.DefinitionRevision, fixture.Clock.GetUtcNow())
            : new PauseSubscription(subscription.Id, subscription.DefinitionRevision, fixture.Clock.GetUtcNow()), default);

        var replay = Assert.Single((await fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([Item(fixture, "012")]), default)).Items);

        Assert.False(replay.Created);
        Assert.Equal(subscription.Id, replay.Subscription.Id);
        Assert.Equal(subscription.Status, replay.Subscription.Status);
        Assert.Equal(subscription.DefinitionRevision, replay.Subscription.DefinitionRevision);
        Assert.Equal("Student's new name", replay.Subscription.Label);
        Assert.Equal(1, replay.Subscription.ActivationGeneration);
    }

    [Fact]
    public async Task ASeparateExplicitFullTargetIsADifferentMappingWithoutRemovingTheFirst()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = Item(fixture, "012");
        var nextTarget = new SectionKey(2026, first.ConfirmedTarget.SemesterCode, "U000300003", "F32.102", "012", "1");
        var second = new LegacyImportItem(first.Source, nextTarget,
            new NewSubscription(Guid.NewGuid(), "Explicit other term", SubscriptionKind.Section, [nextTarget], false, null, fixture.Clock.GetUtcNow()));

        var original = Assert.Single((await fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([first]), default)).Items);
        var another = Assert.Single((await fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([second]), default)).Items);

        Assert.True(another.Created);
        Assert.NotEqual(original.Subscription.Id, another.Subscription.Id);
        Assert.Equal(2, (await fixture.Store.ListSubscriptionsAsync(default)).Length);
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.Subscription, 2)]
    [InlineData(SqliteStoreWriteStage.ImportLedger, 1)]
    [InlineData(SqliteStoreWriteStage.ImportLedger, 2)]
    public async Task BatchFailureRollsBackEverySubscriptionMemberAndLedgerRow(SqliteStoreWriteStage target, int failureAt)
    {
        var armed = false;
        var seen = 0;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == target && ++seen == failureAt) throw new ImportFailure();
        });
        var first = Item(fixture, "012");
        var second = Item(fixture, "013");
        var issued = await fixture.IssueAsync(first.ConfirmedTarget);
        var batch = new LegacyImportBatch([first, second]);
        armed = true;

        await Assert.ThrowsAsync<ImportFailure>(() => fixture.Store.ImportLegacyBatchAsync(batch, default));

        armed = false;
        await fixture.ReopenAsync();
        Assert.Empty(await fixture.Store.ListSubscriptionsAsync(default));
        Assert.Equal(0, (await fixture.Store.ReadCommitContextAsync(issued.Token, first.ConfirmedTarget, default)).CatalogRevision);
        using (var verify = new SqliteConnectionFactory(fixture.DatabasePath).Open(SqliteOpenMode.ReadOnly))
        using (var command = verify.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM LegacyImports;";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        var receipt = await fixture.Store.ImportLegacyBatchAsync(batch, default);
        Assert.Equal(2, receipt.Items.Count(i => i.Created));
        Assert.Equal(2, (await fixture.Store.ListSubscriptionsAsync(default)).Length);
    }

    [Fact]
    public async Task ConflictingManualIdentityDoesNotLeaveTheEarlierBatchItemImported()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = Item(fixture, "012");
        var second = Item(fixture, "013");
        var manual = new NewSubscription(second.Creation.Id, "Existing manual choice", SubscriptionKind.Section,
            [second.ConfirmedTarget], false, null, fixture.Clock.GetUtcNow());
        await fixture.Store.CreateSubscriptionAsync(manual, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([first, second]), default));

        Assert.Equal(manual.Id, Assert.Single(await fixture.Store.ListSubscriptionsAsync(default)).Id);
        var retryFirst = Assert.Single((await fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([first]), default)).Items);
        Assert.True(retryFirst.Created);
    }

    [Fact]
    public async Task OnlyANewIssuedCheckCanInitializeImportedMembershipOrCreateAnAlert()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var item = Item(fixture, "012", historyRegistered: 110);
        var inflight = await fixture.IssueAsync(item.ConfirmedTarget);
        var imported = Assert.Single((await fixture.Store.ImportLegacyBatchAsync(new LegacyImportBatch([item]), default)).Items);
        Assert.Equal(inflight.Token.Ordinal, Assert.Single(imported.Subscription.Members).ActivationWatermark);

        Assert.Null((await fixture.CommitAsync(fixture.Counts(inflight, item.ConfirmedTarget, 119))).Receipt!.EventId);
        Assert.Null(Assert.Single((await fixture.Store.FindSubscriptionAsync(imported.Subscription.Id, default))!.Members).InitializedByAttemptOrdinal);
        var fresh = await fixture.IssueAsync(item.ConfirmedTarget);
        Assert.NotNull((await fixture.CommitAsync(fixture.Counts(fresh, item.ConfirmedTarget, 119))).Receipt!.EventId);
        Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
    }

    private static LegacyImportItem Item(StoreFixture fixture, string section, int historyRegistered = 100, string label = "Imported statistics")
    {
        var key = StoreFixture.Key(section: section);
        var history = new LegacyHistoricalState("Historical statistics", historyRegistered, 120, LegacyAvailability.Available,
            "2026-10-31T12:00:00+09:00", new DateTimeOffset(2026, 10, 31, 3, 0, 0, TimeSpan.Zero));
        return new LegacyImportItem(new LegacyCourseIdentity(2026, "U000200002", key.CourseCode, section), key,
            new NewSubscription(Guid.NewGuid(), label, SubscriptionKind.Section, [key], true, null, fixture.Clock.GetUtcNow()), history);
    }

    private sealed class ImportFailure : Exception;
}
