using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Infrastructure.Snu;
using SnuWatcher.Infrastructure.Snu.Gateway;

namespace SnuWatcher.Windows.Hosting;

public sealed partial class WindowsMonitoringHost
{
    /// <summary>
    /// Opens only a marked private profile with the real store-backed source/parser/gateway. The handler
    /// factory runs only after database ownership and store creation; qualification supplies a sealed
    /// fixture-only handler. No normal-profile factory, socket fallback or pacing bypass is introduced.
    /// </summary>
    public static Task<WindowsHostOpenResult> OpenOfflineSnuValidationAsync(RuntimeProfile profile,
        Func<HttpMessageHandler> transportFactory, IBatchDesktopAlertChannel channel, TimeProvider? clock = null,
        RuntimeSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(transportFactory); ArgumentNullException.ThrowIfNull(channel);
        if (!profile.IsValidation) throw new ArgumentException("A marked private validation profile is required.", nameof(profile));
        return OpenCoreAsync(profile, channel, new(OpenStoreAsync, (store, actualClock, ct) =>
            OpenOfflineSourceAsync(store, actualClock, transportFactory, ct)), clock ?? TimeProvider.System, options ?? new(), cancellationToken);
    }

    private static async Task<WindowsMonitoringHostSource> OpenOfflineSourceAsync(SqliteWatchStore store, TimeProvider clock,
        Func<HttpMessageHandler> transportFactory, CancellationToken token)
    {
        var handler = transportFactory() ?? throw new InvalidOperationException("The offline transport factory returned no handler.");
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        var source = await SnuCourseSource.CreateWithTransportAsync(client, store, timeProvider: clock, cancellationToken: token).ConfigureAwait(false);
        try
        {
            var gateway = new SnuCourseGateway(source, clock);
            return new(gateway, gateway.DisposeAsync, source.DisposeAsync, () => source.SourcePolicyDiagnosticCode,
                ct => source.RecoverSourcePolicyAsync(ct), (revision, ct) => source.ResumeSourceAccessAsync(revision, ct));
        }
        catch { await source.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
