using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Presentation.Shell;
using SnuWatcher.Windows.Presentation.Watches;
using Forms = System.Windows.Forms;

namespace SnuWatcher.Desktop.Runtime;

internal sealed record StudentUiBox(double Left, double Top, double Width, double Height)
{
    internal static StudentUiBox From(Rect value) => new(value.Left, value.Top, value.Width, value.Height);
    internal Rect Rectangle => new(Left, Top, Width, Height);
}

/// <summary>Actual OS-sized checks are independent from the separately labeled reference bitmap captures.</summary>
internal sealed record StudentUiWorkArea
{
    public double ActualOsDpiX { get; init; }
    public double ActualOsDpiY { get; init; }
    public int PrimaryScreenPixelWidth { get; init; }
    public int PrimaryScreenPixelHeight { get; init; }
    public StudentUiBox WorkAreaBounds { get; init; } = new(0, 0, 0, 0);
    public StudentUiBox StartupWindowBounds { get; init; } = new(0, 0, 0, 0);
    public bool StartupBoundsWithinWorkArea { get; init; }
    public StudentUiBox? RestoredWindowBounds { get; init; }
    public StudentUiBox? RestoredWorkAreaBounds { get; init; }
    public bool RestoredBoundsWithinWorkArea { get; init; }
    public bool SearchRowReachable { get; init; }
    public bool SearchControlsReachable { get; init; }
    public bool WatchesRowReachable { get; init; }
    public bool WatchesControlsReachable { get; init; }
    public bool SettingsControlsReachable { get; init; }
    public bool QuitControlReachable { get; init; }
}

internal static partial class StudentUiValidation
{
    private static StudentUiWorkArea ReadStartupWorkArea(StudentWindow window)
    {
        var area = SystemParameters.WorkArea;
        var bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var screen = Forms.Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("ui_primary_screen_unavailable");
        return new()
        {
            ActualOsDpiX = dpi.PixelsPerInchX, ActualOsDpiY = dpi.PixelsPerInchY,
            PrimaryScreenPixelWidth = screen.Width, PrimaryScreenPixelHeight = screen.Height,
            WorkAreaBounds = StudentUiBox.From(area), StartupWindowBounds = StudentUiBox.From(bounds),
            StartupBoundsWithinWorkArea = FitsWorkArea(bounds, area),
        };
    }

    private static bool FitsWorkArea(Rect window, Rect area) => window.Width > 0 && window.Height > 0
        && window.Left >= area.Left - 1 && window.Top >= area.Top - 1 && window.Right <= area.Right + 1 && window.Bottom <= area.Bottom + 1;

    private static async Task<StudentUiWorkArea> VerifyActualWorkAreaAsync(RuntimeProfile profile, List<StudentUiCapture> captures,
        StudentWindow window, StudentDesktopSession graph,
        StudentUiValidationSource source, StudentUiJourney proof, StudentUiWorkArea initial,
        Action<StudentUiWorkArea> progress, CancellationToken ct)
    {
        _stage = "actual_work_area_restore";
        // Restore exactly the production constructor's observed fit, rather than silently substituting a larger resolution.
        window.Width = initial.StartupWindowBounds.Width; window.Height = initial.StartupWindowBounds.Height;
        window.Left = initial.StartupWindowBounds.Left; window.Top = initial.StartupWindowBounds.Top;
        await PumpUiAsync(window);
        var bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        var area = SystemParameters.WorkArea;
        var result = initial with { RestoredWindowBounds = StudentUiBox.From(bounds), RestoredWorkAreaBounds = StudentUiBox.From(area),
            RestoredBoundsWithinWorkArea = FitsWorkArea(bounds, area) };
        progress(result);
        Require(initial.StartupBoundsWithinWorkArea && result.RestoredBoundsWithinWorkArea, "ui_actual_window_outside_work_area");

        _stage = "actual_work_area_search";
        source.FailInteractiveSearch = false;
        await NavigateAsync(window, graph, StudentPage.FindCourses);
        await ExpandFiltersAsync(window);
        SetText(window, "YearField", StudentUiValidationSource.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (graph.Search.SelectedTerm is null) await LoadTermAsync(window, graph, ct);
        SetText(window, "CodeField", StudentUiValidationSource.Key012.CourseCode); SetText(window, "SectionField", "012");
        foreach (var name in new[] { "YearField", "TermField", "CodeField", "SectionField" })
            await RequireReachableAsync(Named<FrameworkElement>(window, name), window, "ui_actual_search_control_unreachable");
        await RequireReachableAsync(BoundButton(window, graph.Search.SearchCommand), window, "ui_actual_search_button_unreachable");
        await InvokeCommandAsync(window, graph.Search.SearchCommand, ct);
        await ShowSearchPaneAsync(window, details: false);
        RequireVisibleRow(Named<DataGrid>(window, "ResultsGrid"), window,
            item => item is CourseSearchItem row && row.Row.Key == StudentUiValidationSource.Key012,
            "ui_actual_search_row_unreachable", requireIdentifier: true);
        result = result with { SearchRowReachable = true, SearchControlsReachable = true }; progress(result);
        captures.Add(await CaptureActualAsync(profile, window, "ui-actual-search.png", ct));

        _stage = "actual_work_area_watches";
        await NavigateAsync(window, graph, StudentPage.MyWatches);
        await EventuallyAsync(() => graph.Watches.State.Rows.Any(row => row.Id == proof.WatchId)
            && !graph.Watches.State.IsLoading && !graph.Watches.State.SelectionPending, ct);
        var listNavigation = Visuals<Button>(window).FirstOrDefault(button => button.IsVisible && button.Name == "ListNavigation");
        if (listNavigation is not null) { InvokeButton(listNavigation); await PumpUiAsync(window); }
        var grid = Visuals<DataGrid>(window).FirstOrDefault(candidate => candidate.IsVisible && candidate.Items.OfType<WatchRow>().Any())
            ?? throw new InvalidOperationException("ui_actual_watch_list_missing");
        RequireVisibleRow(grid, window, item => item is WatchRow row && row.Id == proof.WatchId,
            "ui_actual_watch_row_unreachable", requireIdentifier: false);
        await RequireReachableAsync(BoundButton(window, graph.Watches.ReloadCommand), window, "ui_actual_watch_reload_unreachable");
        captures.Add(await CaptureActualAsync(profile, window, "ui-actual-watches-list.png", ct));
        var detailsNavigation = Visuals<Button>(window).FirstOrDefault(button => button.IsVisible && button.Name == "DetailsNavigation");
        if (detailsNavigation is not null) { InvokeButton(detailsNavigation); await PumpUiAsync(window); }
        RequireVisibleWatchMember(window);
        await RequireReachableAsync(Named<TextBox>(window, "RenameField"), window, "ui_actual_watch_editor_unreachable");
        await RequireReachableAsync(BoundButton(window, graph.Watches.RenameCommand), window, "ui_actual_watch_action_unreachable");
        result = result with { WatchesRowReachable = true, WatchesControlsReachable = true }; progress(result);
        captures.Add(await CaptureActualAsync(profile, window, "ui-actual-watches-details.png", ct));

        _stage = "actual_work_area_settings";
        await NavigateAsync(window, graph, StudentPage.Settings);
        await EventuallyAsync(() => !graph.Settings.Model.State.IsBusy && !graph.Settings.ReloadCommand.IsExecuting, ct);
        foreach (var field in Visuals<ComboBox>(window).Where(field => field.IsVisible && ReferenceEquals(field.DataContext, graph.Settings)).ToArray())
            await RequireReachableAsync(field, window, "ui_actual_settings_control_unreachable");
        await RequireReachableAsync(BoundButton(window, graph.Settings.SaveCommand), window, "ui_actual_settings_save_unreachable");
        await InvokeCommandAsync(window, graph.Settings.SaveCommand, ct);
        Require(!graph.Settings.Model.State.IsApplyPending && !graph.Settings.Model.State.SaveUncertain, "ui_actual_settings_not_applied");
        var quitText = window.TryFindResource("Ui.Quit") as string;
        var quit = Visuals<Button>(window).First(button => button.IsVisible && button.Command is null && button.Content as string == quitText);
        await RequireReachableAsync(quit, window, "ui_actual_quit_unreachable");
        result = result with { SettingsControlsReachable = true, QuitControlReachable = true }; progress(result);
        captures.Add(await CaptureActualAsync(profile, window, "ui-actual-settings.png", ct));
        Require(graph.LastErrorCode is null, "ui_actual_work_area_graph_failed");
        return result;
    }

    private static async Task ExpandFiltersAsync(StudentWindow window)
    {
        var filters = Visuals<Expander>(window).FirstOrDefault(element => element.IsVisible && element.Name == "FiltersExpander");
        if (filters is not null) filters.IsExpanded = true;
        await PumpUiAsync(window);
    }

    private static async Task RequireReachableAsync(FrameworkElement control, Window window, string code)
    {
        Require(control.IsVisible && control.IsEnabled, code);
        control.BringIntoView(); await PumpUiAsync(window);
        var actual = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
        var visible = VisibleBounds(control, window);
        var tolerant = visible; tolerant.Inflate(1, 1);
        Require(!actual.IsEmpty && actual.Width > 0 && actual.Height >= 20 && tolerant.Contains(actual), code);
        Require(control.Focus(), code + "_focus");
    }
}
