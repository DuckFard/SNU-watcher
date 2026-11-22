using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnuWatcher.Windows.Presentation.Search;

namespace SnuWatcher.Desktop.Views;

/// <summary>Search filters and a virtualized result list with independently scrolling child panels.</summary>
public partial class FindCoursesView : UserControl
{
    private bool replacingSelection;
    private bool compactLayout;
    private bool shortLayout;

    /// <summary>Creates the search surface; the shell supplies the search view model.</summary>
    public FindCoursesView() => InitializeComponent();

    private void OnFiltersPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Return || Keyboard.FocusedElement is not TextBox) return;
        args.Handled = true;
        if (!Validation.GetHasError(YearField) && DataContext is CourseSearchViewModel model && model.SearchCommand.CanExecute(null))
        {
            if (shortLayout) FiltersExpander.IsExpanded = false;
            model.SearchCommand.Execute(null);
        }
    }

    private async void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (replacingSelection || DataContext is not CourseSearchViewModel model) return;
        try { await model.SelectRowAsync((ResultsGrid.SelectedItem as CourseSearchItem)?.Row); }
        catch (OperationCanceledException) { /* The view model owns the canceled details runner. */ }
        catch (ObjectDisposedException) { /* Closing the shell closes selection admission. */ }
    }

    private void OnSectionSelectionClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox check || check.DataContext is not CourseSearchItem item
            || DataContext is not CourseSearchViewModel model) return;
        var selectedKey = (ResultsGrid.SelectedItem as CourseSearchItem)?.Row.Key;
        replacingSelection = true;
        try
        {
            model.ToggleSection(item.Row.Key, check.IsChecked == true);
            if (selectedKey is not null)
                ResultsGrid.SelectedItem = model.Model.State.Rows.FirstOrDefault(row => row.Row.Key == selectedKey);
            check.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        }
        finally { replacingSelection = false; }
    }

    private void OnReviewSelectionClicked(object sender, RoutedEventArgs args)
    {
        SidePanel.SelectedIndex = 1;
        if (compactLayout) CompactPaneSelector.SelectedIndex = 1;
        SidePanel.Focus();
    }

    private void OnSearchClicked(object sender, RoutedEventArgs args)
    {
        if (shortLayout) FiltersExpander.IsExpanded = false;
    }

    private void OnCompactPaneChanged(object sender, SelectionChangedEventArgs args) => ApplyPaneVisibility();

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (FiltersScroller is null || ResultPanel is null) return;
        compactLayout = args.NewSize.Width < 900 || args.NewSize.Height < 440;
        var nowShort = compactLayout || args.NewSize.Height < 520;
        if (nowShort && !shortLayout) FiltersExpander.IsExpanded = false;
        shortLayout = nowShort;
        FiltersScroller.MaxHeight = args.NewSize.Height < 520 ? 88 : 144;
        FilterFields.Columns = args.NewSize.Width < 480 ? 2 : 4;
        CompactPaneSelector.Visibility = compactLayout ? Visibility.Visible : Visibility.Collapsed;
        ResultTiming.Visibility = args.NewSize.Height < 540 ? Visibility.Collapsed : Visibility.Visible;
        ResultFooter.Visibility = args.NewSize.Height < 600 ? Visibility.Collapsed : Visibility.Visible;
        // At very short work areas the pane selector already supplies the Results heading;
        // confirmation remains reachable through Details / Watch, keeping a real row viewport.
        ResultHeader.Visibility = args.NewSize.Height < 320 ? Visibility.Collapsed : Visibility.Visible;
        ResultStatusPanel.Margin = new Thickness(0, 0, 0, args.NewSize.Height < 320 ? 4 : 8);
        ResultColumn.Width = new GridLength(compactLayout ? 1 : 2, GridUnitType.Star);
        SideColumn.Width = compactLayout ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ResultRow.Height = new GridLength(1, GridUnitType.Star);
        SideRow.Height = new GridLength(0);
        Grid.SetColumn(SidePanel, compactLayout ? 0 : 1);
        Grid.SetRow(SidePanel, 0);
        ResultPanel.Margin = compactLayout ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        ApplyPaneVisibility();
    }

    private void ApplyPaneVisibility()
    {
        if (ResultPanel is null || SidePanel is null) return;
        var showResults = !compactLayout || CompactPaneSelector.SelectedIndex != 1;
        ResultPanel.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        SidePanel.Visibility = !compactLayout || !showResults ? Visibility.Visible : Visibility.Collapsed;
    }
}
