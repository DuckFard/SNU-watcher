using System.Net;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Windows.Hosting;
using Xunit;

namespace SnuWatcher.Windows.Tests.Validation;

public sealed class OfflineSnuValidationHostTests
{
    [Fact]
    public async Task BusyOwnerCannotConstructTransportOrCreateADatabase()
    {
        var profile = RuntimeProfile.CreateValidationRun(Guid.NewGuid());
        var acquisition = await DatabaseOwnerGate.TryAcquireAsync(profile.ApplicationId);
        await using var owner = acquisition.Lease!;
        var factories = 0;
        try
        {
            var opened = await WindowsMonitoringHost.OpenOfflineSnuValidationAsync(profile,
                () => { factories++; return new NoSocketHandler(); }, new DisabledChannel());
            Assert.Equal(WindowsHostOpenDisposition.AlreadyOwned, opened.Disposition);
            Assert.Equal(0, factories);
            Assert.False(File.Exists(profile.DatabasePath));
        }
        finally { DeletePrivateProfile(profile); }
    }

    [Fact]
    public async Task EmptyRealSourceHostOwnsAndDrainsTheInjectedNoSocketTransportWithoutRequests()
    {
        var profile = RuntimeProfile.CreateValidationRun(Guid.NewGuid());
        var handler = new NoSocketHandler();
        WindowsMonitoringHost? host = null;
        try
        {
            var opened = await WindowsMonitoringHost.OpenOfflineSnuValidationAsync(profile, () => handler, new DisabledChannel());
            host = opened.Host;
            Assert.NotNull(host);
            Assert.Equal(0, handler.Calls);
            Assert.True((await host!.StopAsync()).IsFinalized);
            await host.DisposeAsync();
            Assert.True(handler.IsDisposed);
            Assert.Equal(0, handler.Calls);
        }
        finally { if (host is not null) await host.DisposeAsync(); DeletePrivateProfile(profile); }
    }

    private sealed class NoSocketHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool IsDisposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("No-watch host must not request fixture data."); }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
    private sealed class DisabledChannel : IBatchDesktopAlertChannel
    {
        public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken token) => Task.FromResult(BatchChannelReadiness.Disabled);
        public Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken token) =>
            throw new InvalidOperationException("No-watch host must not submit notifications.");
    }
    private static void DeletePrivateProfile(RuntimeProfile profile)
    {
        var target = Path.GetFullPath(profile.DirectoryPath);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher-runtime-validation"));
        if (!string.Equals(Path.GetDirectoryName(target), expected, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) throw new InvalidOperationException("Invalid private test cleanup target.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
