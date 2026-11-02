using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Infrastructure.Legacy;

/// <summary>Reads only explicitly selected legacy files. No settings, environment values or source writes are applied.</summary>
public sealed class LegacyWatchReader : ILegacyReader
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action? _snapshotAnchored;
    private readonly Action? _afterSidecarsPinned;

    public LegacyWatchReader() { }
    internal LegacyWatchReader(Action snapshotAnchored, Action? afterSidecarsPinned = null)
    { _snapshotAnchored = snapshotAnchored; _afterSidecarsPinned = afterSidecarsPinned; }

    public async Task<LegacyReadSnapshot> ReadAsync(LegacySourceSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Read(selection, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private LegacyReadSnapshot Read(LegacySourceSelection selection, CancellationToken cancellationToken)
    {
        var diagnostics = new LegacyDiagnostics();
        var configured = selection.ConfigPath is { } config
            ? ReadSource(() => LegacyTomlSource.Read(config, diagnostics, cancellationToken), LegacySourceKind.Configuration, diagnostics)
            : [];
        cancellationToken.ThrowIfCancellationRequested();
        var historical = selection.StatePath is { } database
            ? ReadSource(() => LegacySqliteSource.Read(database, diagnostics, _snapshotAnchored, _afterSidecarsPinned, cancellationToken), LegacySourceKind.StateDatabase, diagnostics)
            : [];
        cancellationToken.ThrowIfCancellationRequested();
        var merged = configured.ToDictionary(d => d.Identity);
        foreach (var definition in historical)
        {
            if (merged.TryGetValue(definition.Identity, out var active))
                merged[definition.Identity] = new(active.Identity, active.Label, true, definition.HistoricalState);
            else merged.Add(definition.Identity, definition);
        }
        if (merged.Count > PersistenceLimits.MaxSections)
        {
            diagnostics.Add(LegacySourceKind.StateDatabase, "legacy_combined_rows_exceeded");
            return new LegacyReadSnapshot(configured, diagnostics.Items);
        }
        return new LegacyReadSnapshot(merged.Values, diagnostics.Items);
    }

    private static List<LegacyDefinition> ReadSource(Func<List<LegacyDefinition>> read, LegacySourceKind source,
        LegacyDiagnostics diagnostics)
    {
        try { return read(); }
        catch (LegacySourceException error) { diagnostics.Add(source, error.Code); }
        catch (SqliteException) { diagnostics.Add(source, "legacy_database_unavailable"); }
        catch (IOException) { diagnostics.Add(source, "legacy_source_unavailable"); }
        catch (UnauthorizedAccessException) { diagnostics.Add(source, "legacy_source_unavailable"); }
        catch (NotSupportedException) { diagnostics.Add(source, "legacy_source_unsupported"); }
        return [];
    }
}

internal sealed class LegacyDiagnostics
{
    private readonly List<LegacyDiagnostic> _items = [];
    internal IReadOnlyList<LegacyDiagnostic> Items => _items;

    internal void Add(LegacySourceKind source, string code, int? row = null, int? line = null, int? column = null)
    {
        if (_items.Count < PersistenceLimits.MaxSections) _items.Add(new(source, code, row, line, column));
        else _items[^1] = new(source, "legacy_diagnostics_limited");
    }
}
