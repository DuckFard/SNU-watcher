using System;
using System.Threading;
using System.Threading.Tasks;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Presentation.Shell;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>Only the two fixed, host-owned source controls are available to the shell.</summary>
internal sealed class DesktopSourceControls(WindowsMonitoringHost host) : ISourceUiControls
{
    private readonly WindowsMonitoringHost _host = host ?? throw new ArgumentNullException(nameof(host));
    public Task<SourceAccessResumeResult> ResumeSourceAccessAsync(long expectedRevision, CancellationToken cancellationToken = default) =>
        _host.ResumeSourceAccessAsync(expectedRevision, cancellationToken);
    public Task RecoverSourcePolicyAsync(CancellationToken cancellationToken = default) =>
        _host.RecoverSourcePolicyAsync(cancellationToken);
}
