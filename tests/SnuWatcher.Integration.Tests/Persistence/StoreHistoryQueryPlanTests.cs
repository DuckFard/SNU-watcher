using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreHistoryQueryPlanTests
{
    [Fact]
    public async Task RealTakePlusOneTupleSeekDoesNotScanHistoryOrTwoThousandEqualOrdinals()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await StoreHistoryLedgerFixture.SeedAsync(fixture);
        var history = (IAlertHistoryReader)fixture.Store;
        var first = await history.ReadPageAsync();
        Assert.Equal(25, first.Items.Length); Assert.True(first.HasMore);
        Assert.Equal(3, first.HighWaterOrdinal);
        var second = await history.ReadPageAsync(first.NextCursor);
        Assert.Equal(25, second.Items.Length);
        Assert.Equal(50, first.Items.Concat(second.Items).Select(item => item.EventId).Distinct().Count());
        using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open();
        using (var plan = connection.CreateCommand())
        {
            plan.CommandText = "EXPLAIN QUERY PLAN " + StoreHistoryQueries.LaterIds;
            plan.Parameters.AddWithValue("$ordinal", first.NextCursor!.AfterOrdinal);
            plan.Parameters.AddWithValue("$id", first.NextCursor.AfterEventId.ToString("N")); plan.Parameters.AddWithValue("$take", 26);
            using var rows = plan.ExecuteReader();
            var details = new List<string>(); while (rows.Read()) details.Add(rows.GetString(3));
            Assert.Contains(details, detail => detail.Contains("AlertHistoryOrder ((triggerOrdinal,id)<(?,?))", StringComparison.Ordinal));
            Assert.DoesNotContain(details, detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal) || detail.StartsWith("SCAN e", StringComparison.Ordinal));
        }
        var database = connection.Handle!.DangerousGetHandle();
        Assert.Equal(0, sqlite3_prepare_v2(database, StoreHistoryQueries.LaterIds, -1, out var statement, IntPtr.Zero));
        try
        {
            Bind(statement, "$ordinal", first.NextCursor!.AfterOrdinal);
            Assert.Equal(0, sqlite3_bind_text(statement, sqlite3_bind_parameter_index(statement, "$id"), first.NextCursor.AfterEventId.ToString("N"), -1, new IntPtr(-1)));
            Bind(statement, "$take", 26);
            var count = 0; int step; while ((step = sqlite3_step(statement)) == 100) count++;
            Assert.Equal(101, step); Assert.Equal(26, count);
            Assert.Equal(0, sqlite3_stmt_status(statement, 1, 0));
            Assert.Equal(0, sqlite3_stmt_status(statement, 2, 0));
            Assert.InRange(sqlite3_stmt_status(statement, 4, 0), 1, 700);
        }
        finally { sqlite3_finalize(statement); }
    }
    private static void Bind(IntPtr statement, string name, long value) =>
        Assert.Equal(0, sqlite3_bind_int64(statement, sqlite3_bind_parameter_index(statement, name), value));
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_parameter_index(IntPtr statement, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length, IntPtr destructor);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_stmt_status(IntPtr statement, int operation, int reset);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
}
