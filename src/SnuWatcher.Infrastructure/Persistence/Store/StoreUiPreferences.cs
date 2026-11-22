using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore : IUiPreferencesStore
{
    Task<UiPreferencesSnapshot> IUiPreferencesStore.ReadAsync(CancellationToken cancellationToken) =>
        _lane.ExecuteAsync(connection => ReadUiPreferences(connection, null), cancellationToken);

    public Task<UiPreferencesSaveResult> TrySaveAsync(SaveUiPreferencesCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var facts = StoreUiWire.CommandFacts(command);
            UiPreferencesSaveReceipt? replay = null;
            using (var query = Command(connection, transaction, "SELECT commandFacts,receipt FROM UiPreferenceWrites WHERE commandId=$id;",
                ("$id", command.CommandId.ToString("N"))))
            using (var reader = query.ExecuteReader())
                if (reader.Read())
                {
                    var accepted = StoreUiWire.ReadCommandFacts(reader.GetString(0));
                    if (accepted.CommandId != command.CommandId) throw new InvalidDataException("The accepted preference identity is inconsistent.");
                    replay = StoreUiWire.ReadReceipt(reader.GetString(1));
                    if (replay.CommandId != command.CommandId || replay.PreviousRevision != accepted.ExpectedRevision
                        || replay.SavedSnapshot.Values != accepted.Values)
                        throw new InvalidDataException("The preference receipt contradicts its accepted command.");
                    // Only a self-consistent accepted ledger can classify a changed incoming retry
                    // as an expected command conflict; contradictory durable facts are storage failure.
                    if (StoreUiWire.CommandFacts(accepted) != facts) throw new UiPreferencesReplayConflictException();
                }
            var current = ReadUiPreferences(connection, transaction);
            if (replay is not null) return new UiPreferencesSaveResult(UiPreferencesSaveStatus.Replayed, replay, current);
            if (current.Revision != command.ExpectedRevision)
                return new UiPreferencesSaveResult(UiPreferencesSaveStatus.VersionConflict, null, current);
            var saved = new UiPreferencesSnapshot(checked(current.Revision + 1), command.Values, _time.GetUtcNow());
            var receipt = new UiPreferencesSaveReceipt(command.CommandId, current.Revision, saved);
            Run(connection, transaction, "INSERT INTO UiPreferences(id,revision,savedUtc,valuesJson) VALUES(1,$revision,$saved,$values) "
                + "ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,savedUtc=excluded.savedUtc,valuesJson=excluded.valuesJson;",
                ("$revision", saved.Revision), ("$saved", StoreWire.At(saved.SavedAtUtc!.Value)), ("$values", StoreUiWire.Values(command.Values)));
            Checkpoint(SqliteStoreWriteStage.UiPreferencesValues);
            Run(connection, transaction, "INSERT INTO UiPreferenceWrites(commandId,commandFacts,receipt) VALUES($id,$facts,$receipt);",
                ("$id", command.CommandId.ToString("N")), ("$facts", facts), ("$receipt", StoreUiWire.Receipt(receipt)));
            Checkpoint(SqliteStoreWriteStage.UiPreferencesReceipt);
            transaction.Commit();
            return new UiPreferencesSaveResult(UiPreferencesSaveStatus.Applied, receipt, saved);
        }, cancellationToken);
    }

    private static UiPreferencesSnapshot ReadUiPreferences(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var query = Command(connection, transaction, "SELECT revision,savedUtc,valuesJson FROM UiPreferences WHERE id=1;");
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), StoreUiWire.ReadValues(reader.GetString(2)), StoreWire.Time(reader.GetString(1)))
            : new(0, UiPreferencesValues.Defaults, null);
    }
}
