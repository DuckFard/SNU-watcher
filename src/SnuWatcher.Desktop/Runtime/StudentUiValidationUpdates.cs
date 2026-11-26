using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Presentation.Shell;
using SnuWatcher.Windows.Updates;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>
/// Offline stand-in for GitHub and the registry: one fixed newer release, a switch kept in memory, and the release pages
/// the window asked to open, recorded instead of opened. Nothing here reaches the network, the registry or a browser.
/// </summary>
internal sealed class StudentUiValidationUpdates : IUpdateCheckStore, IUpdateSource
{
    internal const string RunningText = "0.1.0-preview.3";
    internal const string NewestText = "0.2.0";
    internal const string NewestPage = "https://github.com/DuckFard/SNU-watcher/releases/tag/v0.2.0";
    private readonly List<Uri> _opened = [];
    private bool _enabled = true;

    internal StudentUiValidationUpdates(TimeProvider clock)
    {
        Settings = new UpdateSettings(this, this, Parse(RunningText), clock, _opened.Add);
    }

    internal UpdateSettings Settings { get; }
    internal int SourceCalls { get; private set; }
    internal DateTimeOffset? LastChecked { get; private set; }
    internal string? NewestSeen { get; private set; }
    internal IReadOnlyList<Uri> Opened => _opened;

    public bool ReadEnabled() => _enabled;
    public void WriteEnabled(bool enabled) => _enabled = enabled;
    public DateTimeOffset? ReadLastChecked() => LastChecked;
    public string? ReadNewestSeen() => NewestSeen;
    public void WriteChecked(DateTimeOffset checkedAtUtc, string? newestSeen) { LastChecked = checkedAtUtc; NewestSeen = newestSeen; }

    public Task<ReleaseVersion?> GetNewestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceCalls++;
        return Task.FromResult<ReleaseVersion?>(Parse(NewestText));
    }

    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version! : throw new InvalidOperationException("ui_update_fixture_invalid");
}

internal static partial class StudentUiValidation
{
    /// <summary>
    /// A newer release shows the window's notice and the Settings card; the notice's button asks for that release's page,
    /// "Not now" hides only the notice, a second check the same day is not made, and the switch applies at once.
    /// </summary>
    private static async Task VerifyUpdateNoticeUiAsync(StudentWindow window, StudentDesktopSession graph,
        StudentUiValidationUpdates updates, StudentUiValidationSource source, CancellationToken ct)
    {
        _stage = "update_notice";
        var settings = graph.Settings.Updates ?? throw new InvalidOperationException("ui_update_settings_missing");
        Require(ReferenceEquals(settings, updates.Settings), "ui_update_settings_replaced");
        var notice = window.FindName("UpdateNotice") as FrameworkElement ?? throw new InvalidOperationException("ui_missing_UpdateNotice");
        var sourceCalls = source.Calls;
        // Demo mode has no update timer: nothing was checked before this step asked.
        Require(updates.SourceCalls == 0 && updates.LastChecked is null && settings.Available is null && !notice.IsVisible,
            "ui_update_checked_without_request");
        await settings.CheckIfDueAsync(ct);
        await PumpUiAsync(window);
        Require(updates.SourceCalls == 1 && updates.NewestSeen == StudentUiValidationUpdates.NewestText
            && updates.LastChecked is not null, "ui_update_check_not_recorded");
        Require(settings.ShowNotice && notice.IsVisible
            && Visuals<TextBlock>(notice).Any(text => text.IsVisible && text.Text == StudentUiValidationUpdates.NewestText),
            "ui_update_notice_not_shown");
        await settings.CheckIfDueAsync(ct);
        Require(updates.SourceCalls == 1, "ui_update_checked_twice_in_a_day");

        InvokeButton(Named<Button>(window, "UpdateNoticeOpen")); await PumpUiAsync(window);
        Require(updates.Opened.Count == 1 && updates.Opened[0].AbsoluteUri == StudentUiValidationUpdates.NewestPage
            && App.IsReleasePage(updates.Opened[0]), "ui_update_release_page_incorrect");

        InvokeButton(Named<Button>(window, "UpdateNoticeDismiss")); await PumpUiAsync(window);
        Require(!notice.IsVisible && !settings.ShowNotice && settings.AvailableVersion == StudentUiValidationUpdates.NewestText,
            "ui_update_notice_not_dismissed");

        // Settings still names the newer version after the notice is dismissed.
        await NavigateAsync(window, graph, StudentPage.Settings, ct);
        var field = Named<CheckBox>(window, "CheckForUpdatesField");
        var card = Ancestor<Border>(field);
        Require(field.IsChecked == true
            && Visuals<TextBlock>(card).Any(text => text.IsVisible && text.Text == StudentUiValidationUpdates.NewestText),
            "ui_update_settings_card_incorrect");
        Toggle(field); await PumpUiAsync(window);
        Require(!updates.ReadEnabled() && !settings.CheckForUpdates && settings.Available is null
            && !Visuals<TextBlock>(card).Any(text => text.IsVisible && text.Text == StudentUiValidationUpdates.NewestText),
            "ui_update_switch_not_applied");
        await settings.CheckIfDueAsync(ct);
        Require(updates.SourceCalls == 1, "ui_update_checked_while_off");
        Toggle(field); await PumpUiAsync(window);
        Require(updates.ReadEnabled() && settings.CheckForUpdates && !notice.IsVisible, "ui_update_switch_not_restored");
        Require(source.Calls == sourceCalls && updates.Opened.Count == 1, "ui_update_step_side_effects");
    }

    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = System.Windows.Media.VisualTreeHelper.GetParent(element); current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        throw new InvalidOperationException("ui_missing_ancestor");
    }

    private static void Toggle(CheckBox box)
    {
        Require(box.IsVisible && box.IsEnabled, "ui_checkbox_unavailable");
        box.BringIntoView();
        var peer = UIElementAutomationPeer.CreatePeerForElement(box) ?? new CheckBoxAutomationPeer(box);
        var toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider
            ?? throw new InvalidOperationException("ui_checkbox_toggle_unavailable");
        toggle.Toggle(); // In-process WPF automation; no operating-system input is generated.
    }
}
