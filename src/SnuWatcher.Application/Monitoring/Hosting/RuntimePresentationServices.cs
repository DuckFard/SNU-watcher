using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;

namespace SnuWatcher.Application.Monitoring.Hosting;

/// <summary>Host-owned capabilities from the same logical store; session facades never expose these injected ports.</summary>
public sealed record RuntimePresentationServices
{
    public RuntimePresentationServices(IWatchDashboardReader dashboard, IUiPreferencesStore preferences, ITermHintCache termHints)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(termHints);
        Dashboard = dashboard; Preferences = preferences; TermHints = termHints;
    }
    public IWatchDashboardReader Dashboard { get; }
    public IUiPreferencesStore Preferences { get; }
    public ITermHintCache TermHints { get; }
}
