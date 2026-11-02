using System.Net;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Infrastructure.Snu.Gateway;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuEmptyCatalogResponseTests
{
    [Fact]
    public async Task EmptyHttp200WithoutContentTypeFailsClosedAndPersistsOnePhysicalAttemptWithoutCourseState()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        await using var source = await fixture.SourceAsync((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/sugang/cc/cc100ajax.action", request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            Assert.Null(response.Content.Headers.ContentType);
            Assert.Equal(0, response.Content.Headers.ContentLength);
            return Task.FromResult(response);
        });
        await using var gateway = new SnuCourseGateway(source, fixture.Clock);

        var read = await fixture.AfterStartupAsync(gateway.GetCatalogAsync(new CatalogQuery(2026),
            SourceRequestPurpose.Interactive, CancellationToken.None));

        Assert.Equal(SourceReadStatus.Failed, read.Status);
        Assert.Null(read.Data);
        var problem = Assert.Single(read.Problems);
        Assert.Equal(SourceProblemKind.ParseChanged, problem.Kind);
        Assert.Equal("source_parse_changed", problem.DiagnosticCode);
        Assert.Null(problem.RetryAfter);
        Assert.Equal(1, fixture.HttpCalls);
        var command = Assert.Single(fixture.Probe.Commands);
        Assert.Equal(SourceRequestOutcome.ParseChanged, command.Outcome);
        Assert.NotNull(command.ActualStartedAtUtc);
        Assert.NotNull(command.ReceivedAtUtc);
        Assert.Null(command.RetryAfter);
        Assert.False(Assert.Single(fixture.Probe.RecordTokensCancelable));

        var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang);
        Assert.Equal(1, policy.Progress.LastIssuedOrdinal);
        Assert.Equal(1, policy.Progress.LatestCompletedOrdinal);
        Assert.Equal(SourceRequestOutcome.ParseChanged, policy.Progress.LatestOutcome);
        Assert.Equal(0, policy.Progress.LastSuccessfulOrdinal);
        Assert.Null(policy.Progress.LastSuccessfulAtUtc);
        Assert.Null(policy.AccessPause);
        Assert.Null(policy.Cooldown);
        Assert.Null(source.SourcePolicyDiagnosticCode);
        Assert.Empty(await fixture.Store.ListSubscriptionsAsync());
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(1));
        var section = await fixture.Store.ReadSectionAsync(new SectionKey(2026,
            "U000200002", "U000300001", "F32.102", "012", "000"));
        Assert.Null(section.Baseline);
        Assert.Null(section.LatestOutcome);
        Assert.Null(section.LastSuccessfulAtUtc);

        using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open(SqliteOpenMode.ReadOnly);
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM SourceRequests;"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM SourceRequestResults;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM IssuedChecks;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM SectionIdentities;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM SectionState;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM Subscriptions;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM Events;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM Outbox;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM DeliveryBatches;"));
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
