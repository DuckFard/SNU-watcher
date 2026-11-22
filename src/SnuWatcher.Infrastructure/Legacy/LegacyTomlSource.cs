using SnuWatcher.Application.Migration;
using Tomlyn;
using Tomlyn.Model;

namespace SnuWatcher.Infrastructure.Legacy;

internal static class LegacyTomlSource
{
    internal static List<LegacyDefinition> Read(string path, LegacyDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var text = LegacySourceFiles.ReadConfiguration(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        TomlTable? document;
        try { document = TomlSerializer.Deserialize<TomlTable>(text, new TomlSerializerOptions { MaxDepth = 16 }); }
        catch (TomlException error)
        {
            diagnostics.Add(LegacySourceKind.Configuration, "legacy_config_syntax_invalid",
                line: error.Line is > 0 ? error.Line : null, column: error.Column is > 0 ? error.Column : null);
            return [];
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (document is null || !document.TryGetValue("courses", out var value) || value is not TomlTableArray courses || courses.Count == 0)
            throw new LegacySourceException("legacy_config_courses_missing");
        if (courses.Count > 20) throw new LegacySourceException("legacy_config_rows_exceeded");
        var definitions = new List<LegacyDefinition>();
        var seen = new HashSet<LegacyCourseIdentity>();
        for (var index = 0; index < courses.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDefinition(courses[index], out var definition))
                diagnostics.Add(LegacySourceKind.Configuration, "legacy_config_row_invalid", index + 1);
            else if (!seen.Add(definition!.Identity))
                diagnostics.Add(LegacySourceKind.Configuration, "legacy_config_duplicate", index + 1);
            else definitions.Add(definition);
        }
        return definitions;
    }

    private static bool TryDefinition(TomlTable table, out LegacyDefinition? definition)
    {
        definition = null;
        if (!table.TryGetValue("year", out var year) || year is not long number || number is < 2000 or > 2200
            || !table.TryGetValue("semester_code", out var semester) || semester is not string semesterCode
            || !table.TryGetValue("course_code", out var course) || course is not string courseCode
            || !table.TryGetValue("section", out var section) || section is not string sectionCode)
            return false;
        try
        {
            var identity = new LegacyCourseIdentity((int)number, semesterCode, courseCode, sectionCode);
            var label = table.TryGetValue("label", out var supplied) ? supplied as string : DisplayLabel(identity);
            if (label is null) return false;
            definition = new(identity, label, true);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    internal static string DisplayLabel(LegacyCourseIdentity identity) => $"{identity.CourseCode}({identity.Section})";
}
