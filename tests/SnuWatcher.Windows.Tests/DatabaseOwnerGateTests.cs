using System.Security.Principal;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class DatabaseOwnerGateTests
{
    private static string ApplicationId() => "SnuWatcher.DatabaseTests." + Guid.NewGuid().ToString("N");
    private static string CurrentSid() => WindowsIdentity.GetCurrent().User!.Value;

    [Fact]
    public async Task IndependentOwnersContendAndDisposalReleasesOnTheOwnershipThread()
    {
        var application = ApplicationId();
        var first = await DatabaseOwnerGate.TryAcquireAsync(application);
        Assert.Equal(DatabaseOwnerDisposition.Acquired, first.Disposition);
        Assert.False(first.WasAbandoned);
        Assert.NotNull(first.Lease);
        try
        {
            var second = await DatabaseOwnerGate.TryAcquireAsync(application);
            Assert.Equal(DatabaseOwnerDisposition.AlreadyOwned, second.Disposition);
            Assert.Null(second.Lease);
            Assert.False(second.WasAbandoned);
        }
        finally { await first.Lease.DisposeAsync(); }

        var replacement = await DatabaseOwnerGate.TryAcquireAsync(application);
        Assert.Equal(DatabaseOwnerDisposition.Acquired, replacement.Disposition);
        Assert.False(replacement.WasAbandoned);
        await replacement.Lease!.DisposeAsync();
        await replacement.Lease.DisposeAsync();
    }

    [Fact]
    public async Task KernelObjectUsesTheGlobalCurrentUserOnlyNamespace()
    {
        var application = ApplicationId();
        var result = await DatabaseOwnerGate.TryAcquireAsync(application);
        await using var lease = result.Lease!;
        var name = DatabaseOwnerGate.BuildMutexName(application, CurrentSid());
        Assert.StartsWith("Global\\", name, StringComparison.Ordinal);
        using var opened = Mutex.OpenExisting(name, new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false,
        });
        Assert.NotNull(opened);
    }

    [Fact]
    public void NamesAreStablePerUserAndSeparateDifferentUserIdentifiers()
    {
        const string first = "S-1-5-21-100-200-300-1001";
        const string second = "S-1-5-21-100-200-300-1002";
        var name = DatabaseOwnerGate.BuildMutexName("DuckFard.SnuWatcher", first);
        Assert.Equal(name, DatabaseOwnerGate.BuildMutexName("DuckFard.SnuWatcher", first));
        Assert.NotEqual(name, DatabaseOwnerGate.BuildMutexName("DuckFard.SnuWatcher", second));
        Assert.DoesNotContain(first, name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Global\\other")]
    [InlineData("bad/name")]
    [InlineData("app\n")]
    public async Task RejectsInvalidNamesBeforeCreatingAKernelObject(string application)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => DatabaseOwnerGate.TryAcquireAsync(application));
    }

    [Fact]
    public async Task PreCanceledAcquisitionDoesNotOwnTheMutex()
    {
        var application = ApplicationId();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseOwnerGate.TryAcquireAsync(application, cancellation.Token));
        var replacement = await DatabaseOwnerGate.TryAcquireAsync(application);
        Assert.Equal(DatabaseOwnerDisposition.Acquired, replacement.Disposition);
        await replacement.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task CancellationAfterKernelAcquisitionCannotLeaveAnOrphanLease()
    {
        var application = ApplicationId();
        using var acquired = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var operation = DatabaseOwnerGate.TryAcquireForTestAsync(application, () =>
        {
            acquired.Set();
            if (!publish.Wait(TimeSpan.FromSeconds(4))) throw new TimeoutException("Test did not release publication.");
        }, cancellation.Token);
        try
        {
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(4)));
            cancellation.Cancel();
        }
        finally { cancellation.Cancel(); publish.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        var replacement = await DatabaseOwnerGate.TryAcquireAsync(application);
        Assert.Equal(DatabaseOwnerDisposition.Acquired, replacement.Disposition);
        Assert.False(replacement.WasAbandoned);
        await replacement.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task RecoversAbandonedKernelOwnershipAndReportsIt()
    {
        var application = ApplicationId();
        var name = DatabaseOwnerGate.BuildMutexName(application, CurrentSid());
        var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false };
        using var keepAlive = new Mutex(false, name, options);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var abandoned = new Mutex(false, name, options);
                if (!abandoned.WaitOne(0)) throw new InvalidOperationException("Unexpected test owner.");
                // Deliberately exit this test thread without ReleaseMutex; keepAlive preserves the object.
            }
            catch (Exception failure) { error = failure; }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(4)));
        Assert.Null(error);

        var result = await DatabaseOwnerGate.TryAcquireAsync(application);
        Assert.Equal(DatabaseOwnerDisposition.Acquired, result.Disposition);
        Assert.True(result.WasAbandoned);
        await result.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentAcquisitionAttemptsProduceOneLease()
    {
        var application = ApplicationId();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => DatabaseOwnerGate.TryAcquireAsync(application)));
        try
        {
            var owner = Assert.Single(results, r => r.Disposition == DatabaseOwnerDisposition.Acquired);
            Assert.NotNull(owner.Lease);
            Assert.Equal(11, results.Count(r => r.Disposition == DatabaseOwnerDisposition.AlreadyOwned && r.Lease is null));
        }
        finally
        {
            foreach (var result in results)
                if (result.Lease is not null) await result.Lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailureAfterKernelAcquisitionReleasesRatherThanAbandonsTheMutex()
    {
        var application = ApplicationId();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseOwnerGate.TryAcquireForTestAsync(application,
            () => throw new InvalidOperationException("Injected startup failure."), CancellationToken.None));
        var replacement = await DatabaseOwnerGate.TryAcquireAsync(application);
        Assert.Equal(DatabaseOwnerDisposition.Acquired, replacement.Disposition);
        Assert.False(replacement.WasAbandoned);
        await replacement.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task AConflictingKernelObjectTypeFailsClosed()
    {
        var application = ApplicationId();
        var name = DatabaseOwnerGate.BuildMutexName(application, CurrentSid());
        using var conflicting = new EventWaitHandle(false, EventResetMode.ManualReset, name,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseOwnerGate.TryAcquireAsync(application));
        Assert.IsType<WaitHandleCannotBeOpenedException>(error.InnerException);
    }

    [Fact]
    public async Task RejectsNamesOverTheBoundAndMalformedSidInTheNameHelper()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => DatabaseOwnerGate.TryAcquireAsync(new string('a', 101)));
        Assert.Throws<ArgumentException>(() => DatabaseOwnerGate.BuildMutexName("DuckFard.SnuWatcher", ""));
        Assert.Throws<ArgumentException>(() => DatabaseOwnerGate.BuildMutexName("DuckFard.SnuWatcher", new string('x', 257)));
        Assert.Throws<ArgumentException>(() => DatabaseOwnerGate.BuildMutexName("DuckFard.SnuWatcher", "invalid\\sid"));
    }
}
