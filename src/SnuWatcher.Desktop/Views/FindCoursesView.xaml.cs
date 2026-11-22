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

    /// <summary>
    /// The first time the page is shown, read the term the registration site reports as current, so the student sees
    /// what they are searching before searching. This is the page's only automatic read; it never runs a search.
    /// </summary>
    private void OnViewVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is true) EnsureCurrentTerm();
    }

    /// <summary>
    /// Starts the current-term read once the shell's local startup (saved settings and hints) has finished, so the
    /// saved year cannot replace a term that was read first. The window calls this again when that startup ends.
    /// </summary>
    internal void EnsureCurrentTerm()
    {
        if (!IsVisible || DataContext is not CourseSearchViewModel model
            || Window.GetWindow(this)?.DataContext is not SnuWatcher.Windows.Presentation.Shell.StudentShellViewModel { State.IsLoaded: true }) return;
        var state = model.Model.State;
        if (state.SelectedTerm is null && state.CatalogState == CourseUiLoadState.Idle && model.LoadCurrentTermCommand.CanExecute(null))
            model.LoadCurrentTermCommand.Execute(null);
    }

    private void OnFiltersPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Return || Keyboard.FocusedElement is not TextBox) return;
        args.Handled = true;
        if (DataContext is CourseSearchViewModel model && model.SearchCommand.CanExecute(null))
        {
            if (shortLayout) FiltersExpander.IsExpanded = false;
            model.SearchCommand.Execute(null);
        }
    }

    /// <summary>Choosing a college reads its departments, as the registration site fills its department list.</summary>
    private void OnCollegeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (DataContext is not CourseSearchViewModel model || model.SelectedCollege is null || !model.Model.State.Departments.IsEmpty) return;
        if (model.LoadCatalogCommand.CanExecute(null)) model.LoadCatalogCommand.Execute(null);
    }

    private async void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (replacingSelection || DataContext is not CourseSearchViewModel model) return;
        // A chosen row shows its details tab, so the side panel always answers the last click.
        if (ResultsGrid.SelectedItem is not null && !compactLayout) SidePanel.SelectedIndex = 0;
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
        // Ticking a section opens the watch tab beside the list, where the selection is confirmed.
        if (!compactLayout && !model.WatchSelection.Model.State.Rows.IsEmpty) SidePanel.SelectedIndex = 1;
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

    /// <summary>"Show every course": clears the filters (the current term stays) and lists the term's first page.</summary>
    private async void OnBrowseAll(object sender, RoutedEventArgs args)
    {
        if (DataContext is not CourseSearchViewModel model) return;
        try
        {
            await model.ClearCommand.ExecuteAsync();
            await model.SearchCommand.ExecuteAsync();
        }
        catch (ObjectDisposedException) { /* Closing the shell closes search admission. */ }
    }

    /// <summary>Puts the caret in the search box, so a student can type straight away.</summary>
    internal bool FocusSearch() => NameField.Focus();

    private void OnFirstPage(object sender, RoutedEventArgs args) => GoToPage(_ => 1);
    private void OnPreviousPage(object sender, RoutedEventArgs args) => GoToPage(state => (state.CurrentPage ?? 1) - 1);
    private void OnNextPage(object sender, RoutedEventArgs args) => GoToPage(state => (state.CurrentPage ?? 1) + 1);
    private void OnLastPage(object sender, RoutedEventArgs args) => GoToPage(state => state.LastPage ?? 1);
    private void OnPageNumber(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: int page }) GoToPage(_ => page);
    }

    private async void GoToPage(Func<CourseSearchSnapshot, int> page)
    {
        if (DataContext is not CourseSearchViewModel model) return;
        try
        {
            await model.GoToPageAsync(page(model.Model.State));
            if (ResultsGrid.Items.Cast<object>().FirstOrDefault() is { } first) ResultsGrid.ScrollIntoView(first);
        }
        catch (ObjectDisposedException) { /* Closing the shell closes paging admission. */ }
    }

    private void OnCompactPaneChanged(object sender, SelectionChangedEventArgs args) => ApplyPaneVisibility();

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (FiltersScroller is null || ResultPanel is null) return;
        compactLayout = args.NewSize.Width < 900 || args.NewSize.Height < 440;
        var nowShort = compactLayout || args.NewSize.Height < 520;
        if (nowShort && !shortLayout) FiltersExpander.IsExpanded = false;
        shortLayout = nowShort;
        FiltersScroller.MaxHeight = args.NewSize.Height < 520 ? 88 : 150;
        FilterFields.Columns = args.NewSize.Width < 560 ? 2 : args.NewSize.Width < 900 ? 3 : 4;
        // Narrow search bars keep the toggle's icon (its accessible name stays "Advanced search").
        AdvancedToggleText.Visibility = args.NewSize.Width < 780 ? Visibility.Collapsed : Visibility.Visible;
        CompactPaneSelector.Visibility = compactLayout ? Visibility.Visible : Visibility.Collapsed;
        ResultTiming.Visibility = args.NewSize.Height < 540 ? Visibility.Collapsed : Visibility.Visible;
        StartPanel.Margin = new Thickness(12, 0, 12, args.NewSize.Height < 520 ? 0 : 24);
        ResultFooter.Visibility = args.NewSize.Height < 600 ? Visibility.Collapsed : Visibility.Visible;
        SearchTitle.Visibility = args.NewSize.Height < 360 ? Visibility.Collapsed : Visibility.Visible;
        // At very short work areas the pane selector already supplies the Results heading;
        // confirmation remains reachable through Details / Watch, keeping a real row viewport.
        ResultHeader.Visibility = args.NewSize.Height < 320 ? Visibility.Collapsed : Visibility.Visible;
        ResultStatusPanel.Margin = new Thickness(0, 0, 0, args.NewSize.Height < 320 ? 4 : 8);
        ResultColumn.Width = new GridLength(compactLayout ? 1 : 3, GridUnitType.Star);
        SideColumn.Width = compactLayout ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        ResultRow.Height = new GridLength(1, GridUnitType.Star);
        SideRow.Height = new GridLength(0);
        Grid.SetColumn(SideStack, compactLayout ? 0 : 1);
        Grid.SetRow(SideStack, 0);
        ResultPanel.Margin = compactLayout ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        CompactWatchButton.Visibility = compactLayout ? Visibility.Visible : Visibility.Collapsed;
        ApplyPaneVisibility();
    }

    private void ApplyPaneVisibility()
    {
        if (ResultPanel is null || SideStack is null) return;
        var showResults = !compactLayout || CompactPaneSelector.SelectedIndex != 1;
        ResultPanel.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        SideStack.Visibility = !compactLayout || !showResults ? Visibility.Visible : Visibility.Collapsed;
    }
}
