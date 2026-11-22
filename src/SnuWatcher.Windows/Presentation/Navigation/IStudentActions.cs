using SnuWatcher.Core;

namespace SnuWatcher.Windows.Presentation.Navigation;

/// <summary>Only the two verified public browser destinations are supported.</summary>
public enum StudentDestination
{
    /// <summary>The official SNU course registration homepage.</summary>
    Registration,
    /// <summary>The official SNU Genie homepage.</summary>
    Genie,
}

/// <summary>
/// UI actions implemented by the desktop boundary. Course/server strings never supply a URL,
/// executable, or shell argument. Validation supplies an explicit recording implementation.
/// </summary>
public interface IStudentActions
{
    /// <summary>Open a fixed official destination in the student's browser.</summary>
    Task OpenDestinationAsync(StudentDestination destination, CancellationToken cancellationToken = default);

    /// <summary>Copy the literal course code and three-digit section; no source title is interpreted.</summary>
    Task CopySectionIdentifierAsync(SectionKey key, CancellationToken cancellationToken = default);
}
