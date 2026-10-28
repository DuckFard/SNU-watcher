using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace SnuWatcher.Windows;

/// <summary>
/// Owns one app process per Windows user/session and forwards bounded actions over a same-user pipe.
/// A dedicated thread owns/releases the mutex because Windows mutex ownership is thread-affine.
/// </summary>
public sealed partial class SingleInstanceCoordinator : IAsyncDisposable
{
    private readonly string _key;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _release = new();
    private Thread? _ownershipThread;
    private Task? _listener;
    private bool _owns;
    private bool _disposed;
    private Exception? _ownershipError;

    /// <summary>Creates coordination names scoped to the current user and Windows session.</summary>
    public SingleInstanceCoordinator(string applicationId)
    {
        if (string.IsNullOrEmpty(applicationId) || applicationId.Length > 100 || !ApplicationIdPattern().IsMatch(applicationId))
            throw new ArgumentException("Application ID must contain letters, digits, periods or hyphens.", nameof(applicationId));
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..24];
        _key = $"{applicationId}.{suffix}.{Process.GetCurrentProcess().SessionId}";
    }

    /// <summary>Non-sensitive transport failures are observable instead of silently swallowed.</summary>
    public event Action<string>? Diagnostic;

    /// <summary>Attempts ownership once; the successful owner must start listening before showing UI.</summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownershipThread is not null)
            return _owns;
        _ownershipThread = new Thread(OwnMutex) { IsBackground = true, Name = "SNU Watcher instance owner" };
        _ownershipThread.Start();
        _ready.Wait();
        if (_ownershipError is not null)
            throw new InvalidOperationException("Instance ownership could not be established.", _ownershipError);
        return _owns;
    }

    private void OwnMutex()
    {
        try
        {
            using var mutex = new Mutex(false, "Local\\" + _key);
            try { _owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { _owns = true; }
            _ready.Set();
            if (!_owns) return;
            _release.Wait();
            mutex.ReleaseMutex();
        }
        catch (Exception error)
        {
            _ownershipError = error;
            _ready.Set();
        }
    }

    /// <summary>Starts an asynchronous same-user listener for exact recognized actions only.</summary>
    public void StartListening(Action<ActivationRequest> onActivation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_owns || _listener is not null)
            throw new InvalidOperationException("Only an acquired owner may start the listener once.");
        _listener = Task.Run(() => ListenAsync(onActivation, _shutdown.Token));
    }

    private async Task ListenAsync(Action<ActivationRequest> onActivation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_key, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var payload = await ReadBoundedLineAsync(pipe, deadline.Token).ConfigureAwait(false);
                var request = ActivationRequest.ParseLocal(payload);
                var acknowledgement = Encoding.UTF8.GetBytes("accepted:" + Environment.ProcessId + "\n");
                await pipe.WriteAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                onActivation(request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or ArgumentException or OperationCanceledException)
            {
                Diagnostic?.Invoke("An invalid or incomplete local activation was ignored.");
            }
        }
    }

    private static async Task<string> ReadBoundedLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[513];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("Activation ended before its line terminator.");
            if (bytes[count] == (byte)'\n')
                return Encoding.UTF8.GetString(bytes, 0, count);
            count++;
        }
        throw new ArgumentException("Activation exceeds the payload limit.");
    }

    /// <summary>Forwards a validated action and returns the acknowledging owner process ID.</summary>
    public async Task<int> ForwardAsync(ActivationRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", _key, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(request.Encode() + "\n");
        await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        var reply = await ReadBoundedLineAsync(pipe, deadline.Token).ConfigureAwait(false);
        if (!reply.StartsWith("accepted:", StringComparison.Ordinal) ||
            !int.TryParse(reply.AsSpan(9), out var ownerProcessId) || ownerProcessId <= 0)
            throw new IOException("The activation owner did not acknowledge the action.");
        return ownerProcessId;
    }

    /// <summary>Stops the listener and releases the owner mutex on its owning thread.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _release.Set();
        if (_listener is not null)
            await _listener.ConfigureAwait(false);
        _ownershipThread?.Join();
        _shutdown.Dispose();
        _ready.Dispose();
        _release.Dispose();
    }

    [GeneratedRegex("^[A-Za-z0-9.-]+$")]
    private static partial Regex ApplicationIdPattern();
}
