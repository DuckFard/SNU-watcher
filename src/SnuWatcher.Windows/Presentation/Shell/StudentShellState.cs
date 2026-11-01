using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Terms;

namespace SnuWatcher.Windows.Presentation.Shell;

/// <summary>The four student surfaces; delivery history remains a separate native-integration milestone.</summary>
public enum StudentPage
{
    /// <summary>Explicit source catalog and course search.</summary>
    FindCourses,
    /// <summary>Bounded saved watch definitions and current member facts.</summary>
    MyWatches,
    /// <summary>Inactive delivery-history explanation.</summary>
    Alerts,
    /// <summary>Local preferences and their applied actor state.</summary>
    Settings,
}

/// <summary>Honest placeholder rather than synthetic alert history.</summary>
public sealed record StudentAlertsPlaceholder
{
    /// <summary>Localized milestone explanation.</summary>
    public string MessageKey => "Ui.AlertsComingSoon";
}

/// <summary>One immutable shell replacement; cached terms are display hints and never catalog authority.</summary>
/// <param name="SelectedPage">Current navigation destination.</param>
/// <param name="IsLoaded">Whether local startup reads completed successfully.</param>
/// <param name="IsDemo">Explicit private demonstration label.</param>
/// <param name="ShowOnboarding">Whether a known onboarding completion has not been saved.</param>
/// <param name="Language">Known saved interface language.</param>
/// <param name="CloseBehavior">Known saved close preference for the desktop host.</param>
/// <param name="TermHints">Display-only offline terms, never authoritative catalog choices.</param>
/// <param name="Source">Merged local/durable source status without section-count freshness claims.</param>
/// <param name="IssueKey">Fixed localized shell operation message.</param>
public sealed record StudentShellState(StudentPage SelectedPage, bool IsLoaded, bool IsDemo, bool ShowOnboarding,
    UiLanguage Language, UiCloseBehavior CloseBehavior, TermHintCacheSnapshot? TermHints, ShellSourceStatus Source,
    string? IssueKey);
