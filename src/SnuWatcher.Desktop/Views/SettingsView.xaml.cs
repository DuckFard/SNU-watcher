using System;
using System.Windows;
using System.Windows.Controls;
using SnuWatcher.Windows.Presentation.Settings;

namespace SnuWatcher.Desktop.Views;

/// <summary>Bindings only; preference writes and profile ownership remain in the settings view model.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Creates the settings surface after the shell installs its shared theme and language resources.</summary>
    public SettingsView() => InitializeComponent();

    private void OnNativeTestConfirmation(object sender, RoutedEventArgs args)
    {
        if (DataContext is SettingsViewModel model && sender is Button { Tag: Guid testId,
            CommandParameter: NativeTestBannerConfirmation confirmation })
            model.ConfirmNativeTest(testId, confirmation);
    }
}
