using System.ComponentModel;
using System.Security;
using System.Text.Json;
using SnuWatcher.Windows.Updates;

namespace SnuWatcher.Windows.Presentation.Settings;

/// <summary>
/// The "check for new versions" switch and what the check found. A check runs at most once a day after a success, and
/// at most once an hour after a failure; failures stay silent, because nothing is wrong with the student's watches.
/// The switch applies immediately, like the sign-in start switch, and is not one of the saved app preferences.
/// </summary>
public sealed class UpdateSettings : INotifyPropertyChanged
{
    /// <summary>A successful check is not repeated sooner than this.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    /// <summary>A failed check is not retried sooner than this.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly IUpdateCheckStore store;
    private readonly IUpdateSource source;
    private readonly TimeProvider clock;
    private readonly Action<Uri> openReleasePage;
    private bool enabled;
    private bool checking;
    private DateTimeOffset? lastAttempt;
    private ReleaseVersion? available;
    private ReleaseVersion? dismissed;
    private string? issueKey;

    /// <summary>
    /// Reads the switch and the last check's result from <paramref name="store"/>; <paramref name="openReleasePage"/>
    /// opens a release page built by <see cref="GitHubReleaseFeed.ReleasePage"/>.
    /// </summary>
    public UpdateSettings(IUpdateCheckStore store, IUpdateSource source, ReleaseVersion current, TimeProvider clock,
        Action<Uri> openReleasePage)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(openReleasePage);
        this.store = store; this.source = source; Current = current; this.clock = clock; this.openReleasePage = openReleasePage;
        enabled = Read(store.ReadEnabled, true);
        // Show what the last check found without asking again.
        if (enabled && ReleaseVersion.TryParse(Read(store.ReadNewestSeen, null), out var seen) && seen! > current) available = seen;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The running version.</summary>
    public ReleaseVersion Current { get; }

    /// <summary>Whether the app checks for new versions; applies immediately.</summary>
    public bool CheckForUpdates
    {
        get => enabled;
        set
        {
            if (value == enabled) return;
            try { store.WriteEnabled(value); enabled = value; issueKey = null; }
            catch (Exception error) when (IsRegistryFailure(error)) { issueKey = "Ui.UpdateSettingFailed"; }
            if (!enabled) available = null;
            Publish();
        }
    }

    /// <summary>A newer published version, or null.</summary>
    public ReleaseVersion? Available => available;

    /// <summary>The newer version as text, for display.</summary>
    public string? AvailableVersion => available?.ToString();

    /// <summary>Whether to show the window's notice: a newer version the student has not dismissed in this session.</summary>
    public bool ShowNotice => available is not null && available != dismissed;

    /// <summary>A resource key describing the last failure to change the switch, or null.</summary>
    public string? IssueKey => issueKey;

    /// <summary>Checks GitHub if the switch is on and a check is due; otherwise returns at once.</summary>
    public async Task CheckIfDueAsync(CancellationToken cancellationToken)
    {
        if (!enabled || checking) return;
        var now = clock.GetUtcNow();
        if (lastAttempt is { } attempt && now - attempt < RetryInterval && now >= attempt) return;
        // A last check in the future (the clock was moved back) does not block checking.
        if (Read(store.ReadLastChecked, null) is { } last && now - last < CheckInterval && now >= last) return;
        checking = true; lastAttempt = now;
        try
        {
            var newest = await source.GetNewestAsync(cancellationToken);
            try { store.WriteChecked(now, newest?.ToString()); }
            catch (Exception error) when (IsRegistryFailure(error)) { /* checked again after the retry interval */ }
            if (!enabled) return;
            available = newest is not null && newest > Current ? newest : null;
            Publish();
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException)
        { /* silent: retried after the retry interval */ }
        finally { checking = false; }
    }

    /// <summary>Opens the newer version's release page on GitHub.</summary>
    public void OpenReleasePage()
    {
        if (available is { } version) openReleasePage(GitHubReleaseFeed.ReleasePage(version));
    }

    /// <summary>Hides the window's notice for this version until the app restarts; Settings still shows it.</summary>
    public void Dismiss()
    {
        if (available is null || available == dismissed) return;
        dismissed = available;
        Publish();
    }

    private void Publish()
    {
        foreach (var name in new[] { nameof(CheckForUpdates), nameof(Available), nameof(AvailableVersion), nameof(ShowNotice), nameof(IssueKey) })
            PropertyChanged?.Invoke(this, new(name));
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception error) when (IsRegistryFailure(error)) { return fallback; }
    }

    private static bool IsRegistryFailure(Exception error) =>
        error is UnauthorizedAccessException or SecurityException or IOException;
}
