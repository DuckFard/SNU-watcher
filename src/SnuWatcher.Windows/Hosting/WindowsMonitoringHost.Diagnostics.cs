using SnuWatcher.Application.Presentation.Diagnostics;

namespace SnuWatcher.Windows.Hosting;

public sealed partial class WindowsMonitoringHost
{
    private readonly IStorageDiagnosticsReader _storageDiagnostics;
    /// <summary>Only fixed owned-profile DB/WAL/SHM lengths. No database path, SQL handle or file content crosses this port.</summary>
    public IStorageDiagnosticsReader StorageDiagnostics => _storageDiagnostics;
}
