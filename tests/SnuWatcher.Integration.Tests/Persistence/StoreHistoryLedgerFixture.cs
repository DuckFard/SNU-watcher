using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;

namespace SnuWatcher.Integration.Tests.Persistence;

/// <summary>Real V5 schema and issued scopes; bounded synthetic event inserts avoid thousands of unrelated observation evaluations.</summary>
internal static class StoreHistoryLedgerFixture
{
    internal static Guid Id(long sequence) => Guid.ParseExact(unchecked((ulong)sequence * 11400714819323198485UL).ToString("x16") + "0000000000000000", "N");
    internal static async Task SeedAsync(StoreFixture fixture, int perOrdinal = 2000, int ordinals = 3)
    {
        var keys = Enumerable.Range(0, perOrdinal).Select(index => StoreFixture.Key("012", "HIST." + index.ToString("D4"))).ToArray();
        var watch = await fixture.Store.CreateSubscriptionAsync(new(Guid.NewGuid(), "통계학 / synthetic sections", SubscriptionKind.Group,
            keys, true, null, fixture.Clock.GetUtcNow()));
        for (var batch = 0; batch < ordinals; batch++)
        {
            var issued = await fixture.IssueAsync(keys);
            using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO Events VALUES($id,$key,$ordinal,$trigger,$at); "
                + "INSERT INTO EventRecipients VALUES($id,$watch,1,$reason); INSERT INTO Outbox VALUES($id,1,0,0);";
            foreach (var name in new[] { "$id", "$key", "$ordinal", "$trigger", "$at", "$watch", "$reason" }) command.Parameters.AddWithValue(name, "");
            for (var index = 0; index < keys.Length; index++)
            {
                var id = Id((long)batch * perOrdinal + index + 1);
                var reason = batch == 0 ? OpeningReason.InitialAvailability : OpeningReason.IncreasedSeats;
                command.Parameters["$id"].Value = id.ToString("N");
                command.Parameters["$key"].Value = StoreWire.Key(keys[index]);
                command.Parameters["$ordinal"].Value = issued.Token.Ordinal;
                command.Parameters["$at"].Value = StoreWire.At(fixture.Clock.GetUtcNow());
                command.Parameters["$watch"].Value = watch.Id.ToString("N");
                command.Parameters["$reason"].Value = (int)reason;
                command.Parameters["$trigger"].Value = StoreWire.Notification(new(keys[index], batch + 1,
                    batch == 0 ? null : batch, issued.Token.Ordinal, fixture.Clock.GetUtcNow(), [new(watch.Id.ToString("N"), 1, reason)]));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            fixture.Clock.Advance(TimeSpan.FromMinutes(-1)); // UTC order must not replace immutable issued order.
        }
    }
}
