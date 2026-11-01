using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    /// <summary>Imports the entire confirmed selection in one transaction. Historical counts never become observations.</summary>
    public Task<LegacyImportReceipt> ImportLegacyBatchAsync(LegacyImportBatch command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            List<LegacyImportItemResult> results = [];
            foreach (var item in command.Items)
            {
                ValidateImport(item);
                var legacyKey = item.Source.CanonicalKey;
                var targetKey = StoreWire.Key(item.ConfirmedTarget);
                var previousId = (string?)Scalar(connection, transaction,
                    "SELECT subscriptionId FROM LegacyImports WHERE legacyKey=$legacy AND targetKey=$target;",
                    ("$legacy", legacyKey), ("$target", targetKey));
                if (previousId is not null)
                {
                    var existing = ReadSubscription(connection, transaction, Guid.ParseExact(previousId, "N"))
                        ?? throw new InvalidDataException("The legacy association has no destination watch.");
                    results.Add(new LegacyImportItemResult(item.Source, item.ConfirmedTarget, existing, false));
                    continue;
                }
                var created = CreateWithinTransaction(connection, transaction, item.Creation);
                Run(connection, transaction,
                    "INSERT INTO LegacyImports(legacyKey,targetKey,subscriptionId,importedUtc,provenance) VALUES($legacy,$target,$subscription,$at,$provenance);",
                    ("$legacy", legacyKey), ("$target", targetKey), ("$subscription", created.Subscription.Id.ToString("N")),
                    ("$at", StoreWire.At(_time.GetUtcNow())), ("$provenance", HistoricalProvenance(item.HistoricalState)));
                Checkpoint(SqliteStoreWriteStage.ImportLedger);
                results.Add(new LegacyImportItemResult(item.Source, item.ConfirmedTarget, created.Subscription, created.Created));
            }
            transaction.Commit();
            return new LegacyImportReceipt(results);
        }, cancellationToken);
    }

    private static void ValidateImport(LegacyImportItem item)
    {
        if (item.Source.Year != item.ConfirmedTarget.Year || item.Source.CourseCode != item.ConfirmedTarget.CourseCode
            || item.Source.Section != item.ConfirmedTarget.Section || item.Creation.Kind != SubscriptionKind.Section
            || item.Creation.Sections.Length != 1 || item.Creation.Sections[0] != item.ConfirmedTarget)
            throw new ArgumentException("Legacy import requires one matching, explicitly selected full section.", nameof(item));
    }

    private static string? HistoricalProvenance(LegacyHistoricalState? history) => history is null ? null : JsonSerializer.Serialize(new
    {
        version = 1, historicalOnly = true, name = history.Name, registered = history.Registered, capacity = history.Capacity,
        availability = (int)history.Availability, originalObservedText = history.OriginalObservedText,
        observedUtc = history.ObservedAtUtc is { } observed ? StoreWire.At(observed) : null,
    });
}
