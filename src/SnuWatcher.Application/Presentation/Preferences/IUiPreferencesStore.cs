namespace SnuWatcher.Application.Presentation.Preferences;

public interface IUiPreferencesStore
{
    /// <summary>Missing state returns revision-zero defaults without writing a row. Unknown/malformed state must fail.</summary>
    Task<UiPreferencesSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Atomically saves values and the original command/receipt. Every accepted save advances one revision.
    /// Identical successful replay precedes CAS and returns the original receipt plus the current snapshot.
    /// </summary>
    /// <exception cref="UiPreferencesReplayConflictException">A successful command identity was reused with different facts.</exception>
    Task<UiPreferencesSaveResult> TrySaveAsync(SaveUiPreferencesCommand command, CancellationToken cancellationToken = default);
}
