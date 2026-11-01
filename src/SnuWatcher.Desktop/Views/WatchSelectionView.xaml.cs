using System.Windows.Controls;

namespace SnuWatcher.Desktop.Views;

/// <summary>Displays the exact immutable selection and the view model's owned watch confirmation command.</summary>
public partial class WatchSelectionView : UserControl
{
    /// <summary>Creates the confirmation surface; no watch is saved during construction.</summary>
    public WatchSelectionView() => InitializeComponent();
}
