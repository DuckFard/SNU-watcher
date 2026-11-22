using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;

namespace SnuWatcher.Application.Presentation.Preferences;

public enum UiLanguage { Korean, English }
public enum UiCloseBehavior { MinimizeToTray, Quit }

/// <summary>An opaque offline selection hint, never an authoritative source catalog term.</summary>
public sealed record AcademicTermHint
{
    public AcademicTermHint(int year, string selector)
    { Year = SourceValidation.Year(year); Selector = SourceValidation.Token(selector, nameof(selector)); }
    public int Year { get; }
    public string Selector { get; }
}

public sealed record UiPreferencesValues
{
    public UiPreferencesValues(UiLanguage language, int? selectedYear, AcademicTermHint? termHint,
        WatchMonitorProfile desiredProfile, UiCloseBehavior closeBehavior, int onboardingVersion, bool notificationSoundMuted = false)
    {
        Language = PersistenceValidation.Defined(language, nameof(language));
        SelectedYear = selectedYear is { } year ? SourceValidation.Year(year) : null;
        if (termHint is not null && termHint.Year != selectedYear)
            throw new ArgumentException("A term hint requires the matching explicitly selected year.", nameof(termHint));
        DesiredProfile = PersistenceValidation.Defined(desiredProfile, nameof(desiredProfile));
        CloseBehavior = PersistenceValidation.Defined(closeBehavior, nameof(closeBehavior));
        if (onboardingVersion is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(onboardingVersion));
        TermHint = termHint; OnboardingVersion = onboardingVersion; NotificationSoundMuted = notificationSoundMuted;
    }
    public UiLanguage Language { get; }
    public int? SelectedYear { get; }
    public AcademicTermHint? TermHint { get; }
    public WatchMonitorProfile DesiredProfile { get; }
    public UiCloseBehavior CloseBehavior { get; }
    public int OnboardingVersion { get; }
    /// <summary>Explicitly suppresses notification audio; false leaves the system's ordinary sound behavior in place.</summary>
    public bool NotificationSoundMuted { get; }
    /// <summary>Stable product defaults, independent of thread culture or the current academic date.</summary>
    public static UiPreferencesValues Defaults { get; } = new(UiLanguage.English, null, null,
        WatchMonitorProfile.Normal, UiCloseBehavior.MinimizeToTray, 0);
}
