using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Migration;

public enum LegacyAvailability { Unknown, Full, Available }
public enum LegacySourceKind { Configuration, StateDatabase }

/// <summary>Historical display/provenance only. This type cannot initialize a baseline or acknowledge delivery.</summary>
public sealed partial record LegacyHistoricalState
{
    public LegacyHistoricalState(string name, int? registered, int? capacity, LegacyAvailability availability,
        string originalObservedText, DateTimeOffset? observedAtUtc)
    {
        Name = LegacyValidation.Text(name, 512, nameof(name), allowEmpty: true);
        if (registered is < 0) throw new ArgumentOutOfRangeException(nameof(registered));
        if (capacity is < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Availability = PersistenceValidation.Defined(availability, nameof(availability));
        OriginalObservedText = LegacyValidation.Text(originalObservedText, 128, nameof(originalObservedText));
        if (observedAtUtc is { } instant && (!OffsetPattern().IsMatch(originalObservedText)
            || !DateTimeOffset.TryParse(originalObservedText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed != instant))
            throw new ArgumentException("UTC provenance requires an explicit, matching source offset.", nameof(observedAtUtc));
        Registered = registered;
        Capacity = capacity;
        ObservedAtUtc = observedAtUtc?.ToUniversalTime();
    }

    public string Name { get; }
    public int? Registered { get; }
    public int? Capacity { get; }
    public LegacyAvailability Availability { get; }
    public string OriginalObservedText { get; }
    public DateTimeOffset? ObservedAtUtc { get; }

    [GeneratedRegex(@"(?:[zZ]|[+-][0-9]{2}:[0-9]{2})\z")]
    private static partial Regex OffsetPattern();
}

public sealed record LegacyDefinition
{
    public LegacyDefinition(LegacyCourseIdentity identity, string label, bool wasConfigured, LegacyHistoricalState? historicalState = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        Label = PersistenceValidation.Label(label);
        WasConfigured = wasConfigured;
        HistoricalState = historicalState;
    }

    public LegacyCourseIdentity Identity { get; }
    public string Label { get; }
    /// <summary>False for state-only rows; historical rows are not automatically selected as active watches.</summary>
    public bool WasConfigured { get; }
    public LegacyHistoricalState? HistoricalState { get; }
}

/// <summary>Safe preview diagnostics contain bounded codes and positions, not file paths or raw input.</summary>
public sealed record LegacyDiagnostic
{
    public LegacyDiagnostic(LegacySourceKind source, string code, int? rowNumber = null, int? lineNumber = null, int? columnNumber = null)
    {
        Source = PersistenceValidation.Defined(source, nameof(source));
        if (string.IsNullOrEmpty(code) || code.Length > 64
            || code.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-')))
            throw new ArgumentException("A diagnostic needs a bounded code.", nameof(code));
        if (rowNumber is <= 0 || lineNumber is <= 0 || columnNumber is <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowNumber), "Diagnostic positions are one-based when present.");
        Code = code;
        RowNumber = rowNumber;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
    }

    public LegacySourceKind Source { get; }
    public string Code { get; }
    public int? RowNumber { get; }
    public int? LineNumber { get; }
    public int? ColumnNumber { get; }
}

/// <summary>Explicit file selections. Infrastructure validates absolute local regular-file paths before reading.</summary>
public sealed record LegacySourceSelection
{
    public LegacySourceSelection(string? configPath = null, string? statePath = null)
    {
        if (configPath is null && statePath is null) throw new ArgumentException("Select at least one legacy source.");
        ConfigPath = configPath is null ? null : LegacyValidation.Text(configPath, 32767, nameof(configPath));
        StatePath = statePath is null ? null : LegacyValidation.Text(statePath, 32767, nameof(statePath));
    }

    public string? ConfigPath { get; }
    public string? StatePath { get; }
}

public sealed record LegacyReadSnapshot
{
    public LegacyReadSnapshot(IEnumerable<LegacyDefinition> definitions, IEnumerable<LegacyDiagnostic> diagnostics)
    {
        Definitions = PersistenceValidation.Snapshot(definitions, PersistenceLimits.MaxSections, nameof(definitions));
        Diagnostics = PersistenceValidation.Snapshot(diagnostics, PersistenceLimits.MaxSections, nameof(diagnostics));
        if (Definitions.Select(d => d.Identity).Distinct().Count() != Definitions.Length)
            throw new ArgumentException("Duplicate definitions must be resolved explicitly in the preview.", nameof(definitions));
    }

    public ImmutableArray<LegacyDefinition> Definitions { get; }
    public ImmutableArray<LegacyDiagnostic> Diagnostics { get; }
}

/// <summary>Bounded readonly legacy input, implemented by Infrastructure. Never initializes the old repository.</summary>
public interface ILegacyReader
{
    Task<LegacyReadSnapshot> ReadAsync(LegacySourceSelection selection, CancellationToken cancellationToken);
}

internal static class LegacyValidation
{
    internal static string Text(string value, int maximum, string name, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || value.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException("Text is missing, oversized or contains control characters.", name);
        return value;
    }
}
