using System.Windows.Controls;

namespace SnuWatcher.Desktop.Views;

/// <summary>Bindings only; preference writes and profile ownership remain in the settings view model.</summary>
public partial class SettingsView : UserControl
{
    /// <summary>Creates the settings surface after the shell installs its shared theme and language resources.</summary>
    public SettingsView() => InitializeComponent();
}
