using System.Runtime.ExceptionServices;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Snu.RequestQueue;

namespace SnuWatcher.Infrastructure.Snu.Policy;

/// <summary>Durable per-physical-request admission and finalization. Its gate is acquired only inside admitted work or explicit controls.</summary>
internal sealed class StoredSourceRequestPolicy(ISourcePolicyStore store, SnuRequestDispatcher queue,
    TimeProvider clock, Func<Task>? afterScopeReleased = null)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private SourceRequestResultCommand? pendingResult;
    private int storageFault;
    private int stopped;
    private long recoveryStarted;
    private int recovering;

    public string? DiagnosticCode => Volatile.Read(ref storageFault) != 0 ? "source_policy_storage_failed" :
        Volatile.Read(ref recovering) != 0 && clock.GetElapsedTime(Interlocked.Read(ref recoveryStarted)) < TimeSpan.FromSeconds(60)
            ? "source_recovering" : null;

    public async Task InitializeAsync(TimeSpan spacing, CancellationToken ct)
    {
        var policy = await ReadAsync(ct).ConfigureAwait(false);
        Import(policy, includeRecovery: true);
        queue.ImportStoredGate(false, spacing < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : spacing);
    }

    public async Task<T> ExecuteAsync<T>(SourceRequestKind kind, Func<SourceTransportContext, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfStopped();
            ThrowIfFaulted();
            SourceRequestIssueResult admission;
            try { admission = await store.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, kind), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { throw LatchFault(); }
            Import(admission.Policy);
            EnsureCanSend(admission, ct);
            var facts = new SourceTransportContext(() => EnsureCanSend(admission, ct));
            SnuSourceException? failure = null;
            T result = default!;
            try { result = await operation(facts, ct).ConfigureAwait(false); }
            catch (Exception) when (facts.ClassifiedFailure is not null) { failure = facts.ClassifiedFailure; }
            catch (SnuSourceException error) { failure = error; }
            // Caller abort without a classified response leaves only its issued intent. No fabricated completion.
            if (failure is not null) queue.ApplyKnownFailure(failure);
            var command = facts.Command(admission.Issued!, clock.GetUtcNow(), failure);
            await RecordAsync(command).ConfigureAwait(false);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
        finally
        {
            serial.Release();
            if (afterScopeReleased is not null) await afterScopeReleased().ConfigureAwait(false);
        }
    }

    private void EnsureCanSend(SourceRequestIssueResult admission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Admission is an awaited durable operation. A UTC rollback can reinstate its older UTC floor.
        // Check again at actual HTTP initiation, after local request/deadline construction as well.
        if (!admission.WasIssued || admission.Policy.IsBlockedAt(clock.GetUtcNow()))
        {
            Import(admission.Policy);
            throw new SourcePolicyBlockedException(admission.Policy.AccessPause is not null ? SourceFailureKind.AccessRestricted : SourceFailureKind.RateLimited,
                Remaining(admission.Policy));
        }
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            if (pendingResult is not null) await RecordAsync(pendingResult).ConfigureAwait(false);
            var policy = await ReadAsync(ct).ConfigureAwait(false);
            Import(policy, includeRecovery: true);
            Volatile.Write(ref storageFault, 0);
            queue.ClearStorageFailure();
        }
        finally { serial.Release(); }
    }

    public async Task<SourceAccessResumeResult> ResumeAsync(long expectedRevision, CancellationToken ct)
    {
        var command = new SourceAccessResumeCommand(SourcePolicyIds.SnuSugang, expectedRevision);
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfStopped(); ThrowIfFaulted();
            SourceAccessResumeResult result;
            try { result = await store.ResumeSourceAccessAsync(command, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { throw LatchFault(); }
            Import(result.Policy, clearPause: result.Applied);
            return result;
        }
        finally { serial.Release(); }
    }

    private async Task<SourcePolicySnapshot> ReadAsync(CancellationToken ct)
    {
        try { return await store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw LatchFault(); }
    }

    private async Task RecordAsync(SourceRequestResultCommand command)
    {
        pendingResult = command;
        try
        {
            await store.RecordSourceRequestResultAsync(command, CancellationToken.None).ConfigureAwait(false);
            pendingResult = null;
        }
        catch (Exception) { throw LatchFault(); }
    }

    private void Import(SourcePolicySnapshot policy, bool clearPause = false, bool includeRecovery = false)
    {
        queue.ImportStoredGate(policy.AccessPause is not null, Remaining(policy), clearPause);
        if (includeRecovery && policy.Progress.LastIssuedOrdinal > policy.Progress.LatestCompletedOrdinal)
        {
            Interlocked.Exchange(ref recoveryStarted, clock.GetTimestamp());
            Volatile.Write(ref recovering, 1);
            queue.ImportStoredGate(false, TimeSpan.FromSeconds(60));
        }
    }

    private TimeSpan Remaining(SourcePolicySnapshot policy)
    {
        var now = clock.GetUtcNow();
        return policy.Cooldown is { } cooldown && cooldown.NotBeforeUtc > now ? cooldown.NotBeforeUtc - now : TimeSpan.Zero;
    }

    private SourcePolicyStorageException LatchFault()
    {
        Volatile.Write(ref storageFault, 1);
        var failure = new SourcePolicyStorageException();
        queue.SetStorageFailure(failure);
        return failure;
    }
    private void ThrowIfFaulted() { if (Volatile.Read(ref storageFault) != 0) throw new SourcePolicyStorageException(); }
    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
    public void StopAcceptingControls() => Interlocked.Exchange(ref stopped, 1);

    /// <summary>Wait for actual admitted control/storage work; keep the managed gate alive for already queued rejected controls.</summary>
    public async Task WaitForIdleAsync()
    {
        await serial.WaitAsync().ConfigureAwait(false);
        serial.Release();
    }
}
