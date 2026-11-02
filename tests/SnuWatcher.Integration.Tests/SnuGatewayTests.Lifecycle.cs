using SnuWatcher.Application.Sources;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed partial class SnuGatewayTests
{
    [Fact]
    public async Task DisposeStopsAdmissionAndWaitsForActualUncooperativeHttpCompletionWithoutOwningSource()
    {
        var started = Barrier(); var release = Barrier();
        await using var fixture = new Harness(async (request, number, token) =>
        {
            if (number == 1) { started.SetResult(); await release.Task; }
            return await DefaultResponse(request, number, token);
        });
        var pending = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
        await started.Task.WaitAsync(Timeout);
        var stopping = fixture.Gateway.DisposeAsync().AsTask();
        try
        {
            Assert.False(stopping.IsCompleted);
            Assert.False(pending.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await stopping.WaitAsync(Timeout);
        await fixture.Gateway.DisposeAsync();
        var stillOwnedByHost = await fixture.Source.GetCatalogAsync(2026);
        Assert.NotEmpty(stillOwnedByHost.Terms);
        Assert.Equal(2, fixture.Handler.Catalogs);
    }

    [Fact]
    public async Task PreCanceledAndInvalidPurposeCallsCannotDispatchOrConsumeCallerCapacity()
    {
        await using var fixture = new Harness(options: new(maxCallers: 1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Watch, canceled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Gateway.SearchAsync(Query, (SourceRequestPurpose)999, default));
        Assert.Equal(0, fixture.Handler.Catalogs);
        Assert.Equal(SourceReadStatus.Complete, (await fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default)).Status);
    }

    [Fact]
    public async Task DisposedInjectedSourceIsAProgrammingErrorRatherThanFabricatedRemoteOutage()
    {
        await using var fixture = new Harness();
        await fixture.Source.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default));
    }

    [Fact]
    public async Task CancellationRacingActualCompletionCannotDisposeTokenEarlyOrMaskACompletedResult()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var started = Barrier(); var release = Barrier();
            await using var fixture = new Harness(async (request, number, token) =>
            { started.SetResult(); await release.Task; return await DefaultResponse(request, number, token); });
            using var cancellation = new CancellationTokenSource();
            var pending = fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, cancellation.Token);
            await started.Task.WaitAsync(Timeout);
            await Task.WhenAll(Task.Run(cancellation.Cancel), Task.Run(() => release.TrySetResult()));
            try { Assert.Equal(SourceReadStatus.Complete, (await pending.WaitAsync(Timeout)).Status); }
            catch (OperationCanceledException) { }
            await fixture.Gateway.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }
}
