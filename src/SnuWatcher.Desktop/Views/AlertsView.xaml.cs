using System;
using System.Windows;
using System.Windows.Controls;
using SnuWatcher.Windows.Presentation.Alerts;

namespace SnuWatcher.Desktop.Views;

/// <summary>Layout and bindings only; history reads and desktop actions belong to the injected controller.</summary>
public partial class AlertsView : UserControl
{
    private bool compact;
    private bool showingDetails;
    private Guid? lastSelectedId;
    /// <summary>Creates the finite, responsive history/result surface.</summary>
    public AlertsView() => InitializeComponent();
    /// <summary>Shows history rows and page navigation in compact mode.</summary>
    public void ShowList() { showingDetails = false; ApplyVisibility(); }
    /// <summary>Shows independently scrolling selected-event details in compact mode.</summary>
    public void ShowDetails() { showingDetails = true; ApplyVisibility(); }
    private void OnShowList(object sender, RoutedEventArgs args) => ShowList();
    private void OnShowDetails(object sender, RoutedEventArgs args) => ShowDetails();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count == 0 || args.AddedItems[0] is not AlertHistoryRow row || row.EventId == lastSelectedId) return;
        lastSelectedId = row.EventId;
        if (compact) ShowDetails();
    }
    private void OnViewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (LayoutGrid is null) return;
        compact = args.NewSize.Width < 780;
        LayoutGrid.ColumnDefinitions[0].Width = new(compact ? 1 : 1.1, GridUnitType.Star);
        LayoutGrid.ColumnDefinitions[1].Width = compact ? new(0) : new(1, GridUnitType.Star);
        Grid.SetColumn(DetailsPanel, compact ? 0 : 1);
        ListPanel.Margin = compact ? new(0) : new(0, 0, 12, 0);
        ApplyVisibility();
    }
    private void ApplyVisibility()
    {
        CompactNavigation.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ListPanel.Visibility = compact && showingDetails ? Visibility.Collapsed : Visibility.Visible;
        DetailsPanel.Visibility = compact && !showingDetails ? Visibility.Collapsed : Visibility.Visible;
        ListNavigation.FontWeight = showingDetails ? FontWeights.Normal : FontWeights.SemiBold;
        DetailsNavigation.FontWeight = showingDetails ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
