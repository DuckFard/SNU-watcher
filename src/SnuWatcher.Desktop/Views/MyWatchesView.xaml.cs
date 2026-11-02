using System;
using System.Windows;
using System.Windows.Controls;
using SnuWatcher.Windows.Presentation.Watches;

namespace SnuWatcher.Desktop.Views;

/// <summary>Bindings only; source requests and watch mutations belong to the injected view model.</summary>
public partial class MyWatchesView : UserControl
{
    private bool? compact;
    private bool showingDetails;
    private Guid? lastSelectedId;

    /// <summary>Creates the responsive watch list/detail surface.</summary>
    public MyWatchesView() => InitializeComponent();

    /// <summary>Shows the bounded list and paging controls in the compact layout.</summary>
    public void ShowList() { showingDetails = false; ApplyPanelVisibility(); }

    /// <summary>Shows the selected watch's independently scrolling details in the compact layout.</summary>
    public void ShowDetails() { showingDetails = true; ApplyPanelVisibility(); }

    private void OnShowList(object sender, RoutedEventArgs args) => ShowList();
    private void OnShowDetails(object sender, RoutedEventArgs args) => ShowDetails();

    private void OnWatchSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count == 0 || args.AddedItems[0] is not WatchRow row || row.Id == lastSelectedId) return;
        lastSelectedId = row.Id;
        if (compact == true) ShowDetails();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (LayoutGrid is null) return;
        var useCompact = args.NewSize.Width < 780;
        if (compact == useCompact) return;
        compact = useCompact;
        LayoutGrid.ColumnDefinitions[0].Width = new(useCompact ? 1 : 1.1, GridUnitType.Star);
        LayoutGrid.ColumnDefinitions[1].Width = useCompact ? new(0) : new(1, GridUnitType.Star);
        Grid.SetColumn(DetailsPanel, useCompact ? 0 : 1);
        ListPanel.Margin = useCompact ? new(0) : new(0, 0, 12, 0);
        ApplyPanelVisibility();
    }

    private void ApplyPanelVisibility()
    {
        if (CompactNavigation is null) return;
        CompactNavigation.Visibility = compact == true ? Visibility.Visible : Visibility.Collapsed;
        ListPanel.Visibility = compact == true && showingDetails ? Visibility.Collapsed : Visibility.Visible;
        DetailsPanel.Visibility = compact == true && !showingDetails ? Visibility.Collapsed : Visibility.Visible;
        ListNavigation.FontWeight = showingDetails ? FontWeights.Normal : FontWeights.SemiBold;
        DetailsNavigation.FontWeight = showingDetails ? FontWeights.SemiBold : FontWeights.Normal;
        ListNavigation.BorderThickness = new(showingDetails ? 1 : 2);
        DetailsNavigation.BorderThickness = new(showingDetails ? 2 : 1);
    }
}
