using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace SnuWatcher.Windows;

/// <summary>Whether this process may open the production per-user monitoring database.</summary>
public enum DatabaseOwnerDisposition
{
    /// <summary>The caller owns the cross-session gate until its lease is disposed.</summary>
    Acquired,
    /// <summary>Another live owner exists; do not open another monitoring database host.</summary>
    AlreadyOwned,
}

/// <summary>Kernel acquisition result. Abandonment is recovery context, not database-integrity proof.</summary>
public sealed record DatabaseOwnerAcquireResult
{
    internal DatabaseOwnerAcquireResult(DatabaseOwnerDisposition disposition, bool wasAbandoned, DatabaseOwnerGate? lease)
    {
        Disposition = disposition;
        WasAbandoned = wasAbandoned;
        Lease = lease;
    }

    /// <summary>Acquired or already owned by another live owner.</summary>
    public DatabaseOwnerDisposition Disposition { get; }
    /// <summary>True when Windows transferred a mutex abandoned by a terminated owner thread.</summary>
    public bool WasAbandoned { get; }
    /// <summary>Present only on acquisition. Dispose after all database work has stopped.</summary>
    public DatabaseOwnerGate? Lease { get; }
}

/// <summary>
/// One database monitoring owner per Windows user across sessions, independently of UI forwarding.
/// A dedicated thread both acquires and releases the mutex; async continuations never own it.
/// </summary>
public sealed partial class DatabaseOwnerGate : IAsyncDisposable
{
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(5);
    private readonly string _name;
    private readonly Action? _beforePublish;
    private readonly TaskCompletionSource<DatabaseOwnerAcquireResult> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DatabaseOwnerGate(string name, Action? beforePublish) { _name = name; _beforePublish = beforePublish; }

    /// <summary>
    /// Attempts immediate ownership using the current Windows SID, never a caller-supplied user/session.
    /// Production callers use one stable application ID. Cancellation also awaits safe owner-thread cleanup.
    /// </summary>
    public static Task<DatabaseOwnerAcquireResult> TryAcquireAsync(string applicationId, CancellationToken cancellationToken = default) =>
        AcquireAsync(applicationId, null, cancellationToken);

    internal static Task<DatabaseOwnerAcquireResult> TryAcquireForTestAsync(string applicationId, Action beforePublish, CancellationToken cancellationToken) =>
        AcquireAsync(applicationId, beforePublish, cancellationToken);

    private static async Task<DatabaseOwnerAcquireResult> AcquireAsync(string applicationId, Action? beforePublish, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        var gate = new DatabaseOwnerGate(BuildMutexName(applicationId, sid), beforePublish);
        var thread = new Thread(gate.OwnMutex) { IsBackground = true, Name = "SNU Watcher database owner" };
        try
        {
            try { thread.Start(); }
            catch { gate._stopped.TrySetResult(); throw; }
            var result = await gate._ready.Task.WaitAsync(HandoffTimeout, cancellationToken).ConfigureAwait(false);
            if (result.Lease is null) await gate.DisposeAsync().ConfigureAwait(false);
            return result;
        }
        catch
        {
            // A cancellation can race the successful WaitOne. Signal the owning thread and wait for
            // its finally block before returning failure, even when no lease reached the caller.
            try { await gate.DisposeAsync().ConfigureAwait(false); }
            finally { if (gate._ready.Task.IsFaulted) _ = gate._ready.Task.Exception; }
            throw;
        }
    }

    private void OwnMutex()
    {
        Mutex? mutex = null;
        var owns = false;
        try
        {
            // Verified against the pinned .NET 10 reference contract. Global access is intentionally
            // cross-session, while runtime-enforced access controls restrict the object to this user.
            mutex = new Mutex(false, _name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
            var abandoned = false;
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; abandoned = true; }
            if (!owns)
            {
                _ready.TrySetResult(new(DatabaseOwnerDisposition.AlreadyOwned, false, null));
                return;
            }
            _beforePublish?.Invoke();
            _ready.TrySetResult(new(DatabaseOwnerDisposition.Acquired, abandoned, this));
            _release.Task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            _ready.TrySetException(new InvalidOperationException("Database ownership could not be established.", error));
        }
        finally
        {
            try
            {
                try { if (owns) mutex!.ReleaseMutex(); }
                finally { mutex?.Dispose(); }
                _stopped.TrySetResult();
            }
            catch (Exception error)
            {
                _stopped.TrySetException(new InvalidOperationException("Database ownership cleanup did not complete.", error));
            }
        }
    }

    /// <summary>Idempotently requests release and waits at most five seconds for owner-thread cleanup.</summary>
    public async ValueTask DisposeAsync()
    {
        _release.TrySetResult();
        await _stopped.Task.WaitAsync(HandoffTimeout).ConfigureAwait(false);
    }

    internal static string BuildMutexName(string applicationId, string sid)
    {
        if (string.IsNullOrEmpty(applicationId) || applicationId.Length > 100 || !ApplicationIdPattern().IsMatch(applicationId))
            throw new ArgumentException("Application ID needs 1-100 ASCII letters, digits, periods or hyphens.", nameof(applicationId));
        if (string.IsNullOrEmpty(sid) || sid.Length > 256) throw new ArgumentException("A Windows SID is required.", nameof(sid));
        var canonicalSid = new SecurityIdentifier(sid).Value;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSid)))[..32];
        return $"Global\\{applicationId}.DatabaseOwner.{suffix}";
    }

    [GeneratedRegex(@"\A[A-Za-z0-9.-]+\z")]
    private static partial Regex ApplicationIdPattern();
}
