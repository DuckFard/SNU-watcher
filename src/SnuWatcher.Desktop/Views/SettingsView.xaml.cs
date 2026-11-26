using System;
using System.Windows;
using System.Windows.Controls;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows.Presentation.Settings;

namespace SnuWatcher.Desktop.Views;

/// <summary>
/// Bindings plus the About card's fixed links; preference writes and profile ownership remain in the settings view model.
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>Creates the settings surface after the shell installs its shared theme and language resources.</summary>
    public SettingsView() => InitializeComponent();

    private void OnOpenStartupSettings(object sender, RoutedEventArgs args) =>
        (DataContext as SettingsViewModel)?.Startup?.OpenWindowsStartupSettings();

    private void OnOpenReleasePage(object sender, RoutedEventArgs args) =>
        (DataContext as SettingsViewModel)?.Updates?.OpenReleasePage();

    private void OnOpenProjectPage(object sender, RoutedEventArgs args) => OpenAbout(AppAbout.ProjectPage.AbsoluteUri);

    private void OnOpenReleaseNotes(object sender, RoutedEventArgs args) => OpenAbout(AppAbout.ReleaseNotes.AbsoluteUri);

    private void OnOpenNotices(object sender, RoutedEventArgs args) => OpenAbout(AppAbout.NoticesPath);

    private void OpenAbout(string target) =>
        AboutIssue.Visibility = AppAbout.TryOpen(target) ? Visibility.Collapsed : Visibility.Visible;

    private void OnNativeTestConfirmation(object sender, RoutedEventArgs args)
    {
        if (DataContext is SettingsViewModel model && sender is Button { Tag: Guid testId,
            CommandParameter: NativeTestBannerConfirmation confirmation })
            model.ConfirmNativeTest(testId, confirmation);
    }
}
