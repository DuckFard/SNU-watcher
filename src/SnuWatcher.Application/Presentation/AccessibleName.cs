namespace SnuWatcher.Application.Presentation;

/// <summary>
/// Short, readable text for an item shown in a list or grid. WPF names such an item after its ToString(), which for a
/// record is otherwise a dump of every property, so this text is what a screen reader announces for the row.
/// </summary>
public static class AccessibleName
{
    /// <summary>The longest name produced; the installed-app UI Automation check accepts up to 256 characters.</summary>
    public const int MaxLength = 200;

    /// <summary>The non-empty parts joined with ", ", cut to <see cref="MaxLength"/> with an ellipsis.</summary>
    public static string Of(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var text = string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
