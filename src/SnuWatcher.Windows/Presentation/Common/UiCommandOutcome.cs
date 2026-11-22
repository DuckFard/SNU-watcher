namespace SnuWatcher.Windows.Presentation.Common;

/// <summary>Command-boundary completion, never a claim that a failed operation rolled back its side effects.</summary>
public enum UiCommandOutcome
{
    /// <summary>The operation and its UI publication completed.</summary>
    Completed,
    /// <summary>The operation cooperatively canceled, or its caller was canceled before admission.</summary>
    Canceled,
    /// <summary>An operation, callback or UI publication failed; only a fixed diagnostic is reported.</summary>
    Failed,
    /// <summary>An earlier execution still owns the command.</summary>
    Busy,
    /// <summary>The predicate rejected execution or admission is permanently closed.</summary>
    Unavailable,
}
