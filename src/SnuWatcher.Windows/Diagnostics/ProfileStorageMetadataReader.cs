using System.Security;
using SnuWatcher.Application.Presentation.Diagnostics;
using SnuWatcher.Windows.Hosting;

namespace SnuWatcher.Windows.Diagnostics;

/// <summary>Reads metadata for the closed profile's fixed database/sidecar leaves. It never enumerates directories or opens file contents.</summary>
public sealed class ProfileStorageMetadataReader : IStorageDiagnosticsReader
{
    private readonly RuntimeProfile profile;
    /// <summary>Accepts a closed host-owned profile rather than an arbitrary filesystem destination.</summary>
    public ProfileStorageMetadataReader(RuntimeProfile profile) => this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
    /// <inheritdoc/>
    public Task<StorageFootprint> ReadAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OrdinaryDirectoryChain(profile.DirectoryPath)) return new StorageFootprint(Unavailable(), Unavailable(), Unavailable());
        return new StorageFootprint(Read(profile.DatabasePath), Read(profile.DatabasePath + "-wal"), Read(profile.DatabasePath + "-shm"));
    }, cancellationToken);

    private static DiagnosticFileSize Read(string path)
    {
        try
        {
            var file = new FileInfo(path); file.Refresh();
            var attributes = file.Attributes;
            // Missing FileInfo attributes are -1; Length supplies the precise missing/error classification.
            if ((int)attributes != -1 && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return Unavailable();
            return new(DiagnosticFileState.Available, file.Length);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(DiagnosticFileState.Missing); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return Unavailable(); }
    }
    private static bool OrdinaryDirectoryChain(string path)
    {
        try
        {
            var current = new DirectoryInfo(path);
            for (var depth = 0; depth < 128; depth++)
            {
                current.Refresh();
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (current.Parent is not { } parent) return true;
                current = parent;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return false; }
    }
    private static DiagnosticFileSize Unavailable() => new(DiagnosticFileState.Unavailable);
}
