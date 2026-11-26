using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SnuWatcher.Application.Monitoring.Health;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Core;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Presentation.Shell;

namespace SnuWatcher.Desktop.Runtime;

internal sealed record StudentUiJourney
{
    public bool LanguageLinks { get; init; }
    public bool EnglishKorean { get; init; }
    public bool LeadingZeroLookup { get; init; }
    public bool DetailsQuotaSeparated { get; init; }
    public bool GroupWatch { get; init; }
    public bool AutoRoutedWatch { get; init; }
    public bool PauseResume { get; init; }
    public bool Rename { get; init; }
    public bool Expiry { get; init; }
    public bool ProfileApplied { get; init; }
    public bool FullVsUnknown { get; init; }
    public bool InteractiveUnavailable { get; init; }
    public bool BrowseAllPaged { get; init; }
    public bool TabTraversal { get; init; }
    public bool EnterSearch { get; init; }
    public bool FixedActionIntents { get; init; }
    public bool TerminalStoppingVisible { get; init; }
    public bool SavedSoundMute { get; init; }
    public bool SimulatedNativeStatus { get; init; }
    public bool SimulatedNativeTest { get; init; }
    public bool SimulatedFeedbackScoped { get; init; }
    public bool SavedAlertHistory { get; init; }
    public bool OriginalAlertIdentitySeats { get; init; }
    public bool BoundedAlertDetails { get; init; }
    public Guid WatchId { get; init; }
    public long PreferencesRevision { get; init; }
    public string Label { get; init; } = "Statistics choices renamed (Demo)";
}

internal static partial class StudentUiValidation
{
    private static async Task<StudentUiJourney> RunJourneyAsync(RuntimeProfile profile, StudentWindow window,
        StudentDesktopSession graph, WindowsMonitoringHost host, StudentUiValidationSource source,
        StudentUiValidationClock clock, StudentUiValidationActions actions, StudentUiBindingProbe bindings,
        StudentUiSimulatedNativeControls simulatedNative, List<StudentUiCapture> captures, CancellationToken ct)
    {
        var proof = new StudentUiJourney();
        // There is no welcome screen: the course page is ready at once, with the language links in the header.
        _stage = "start_language_links";
        Require(graph.Shell.State.IsDemo, "ui_demo_label_missing");
        Require(ReferenceEquals(Keyboard.FocusedElement, Named<TextBox>(window, "NameField")), "ui_start_focus_missing");
        await ProbeTerminalStartAsync(graph, window);
        proof = proof with { TerminalStoppingVisible = true };
        await EnsureCurrentTermAsync(graph, source, ct);
        Require(source.InteractiveSearchCalls == 0 && source.DetailsCalls == 0, "ui_start_ran_search");
        await PumpUiAsync(window);
        captures.Add(await CaptureAsync(profile, window, "ui-start-en.png", false, ct));
        // The language links save only the language: an unsaved Settings edit stays unsaved and unsent.
        var muted = graph.Settings.Model.State.Stored!.Values.NotificationSoundMuted;
        graph.Settings.NotificationSoundMuted = !muted;
        await ClickLanguageAsync(window, graph, UiLanguage.Korean, ct);
        Require((window.TryFindResource("Ui.Settings") as string) == "설정", "ui_korean_resources_missing");
        Require(graph.Settings.Model.State.Stored!.Values.NotificationSoundMuted == muted && graph.Settings.Model.State.IsDirty
            && graph.Settings.NotificationSoundMuted == !muted && graph.Settings.Language == UiLanguage.Korean, "ui_language_link_saved_other_settings");
        // As a student would, wait until Discard is offered (a background settings operation may still be finishing).
        await EventuallyAsync(() => graph.Settings.DiscardChangesCommand.CanExecute(null), ct);
        Require(await graph.Settings.DiscardChangesCommand.ExecuteAsync(ct) == SnuWatcher.Windows.Presentation.Common.UiCommandOutcome.Completed, "ui_language_link_edit_not_discarded");
        Require(!graph.Settings.Model.State.IsDirty, "ui_language_link_edit_not_discarded");
        RequireShownTerm(window, "2026 - 겨울학기 (Demo)", "ui_korean_term_label_missing");
        await ClickLanguageAsync(window, graph, UiLanguage.English, ct);
        Require((window.TryFindResource("Ui.Settings") as string) == "Settings", "ui_english_resources_missing");
        RequireShownTerm(window, "2026 Winter Semester (Demo)", "ui_english_term_label_missing");
        proof = proof with { LanguageLinks = true, EnglishKorean = true };

        // An empty search lists every course in the current term one source page at a time, as the registration site does.
        _stage = "browse_all_paged";
        var searchesBefore = source.InteractiveSearchCalls;
        Require(graph.Search.CourseName.Length == 0 && graph.Search.CourseCode.Length == 0, "ui_browse_filters_not_empty");
        Require(Named<FrameworkElement>(window, "StartPanel").IsVisible, "ui_start_panel_missing");
        await ObserveCommandAsync(graph.Search.SearchCommand, () => InvokeButton(Named<Button>(window, "BrowseAllButton")), ct);
        await PumpUiAsync(window);
        var listing = graph.Search.Model.State;
        Require(listing.Rows.Length == 10 && listing.CurrentPage == 1 && listing.LastPage == 3
            && listing.ResultCount == StudentUiValidationSource.BrowseCount && source.InteractiveSearchCalls == searchesBefore + 1,
            "ui_browse_first_page_missing");
        Require(Named<FrameworkElement>(window, "PagingBar").IsVisible && !Named<Button>(window, "PreviousPageButton").IsEnabled
            && Named<Button>(window, "LastPageButton").IsEnabled, "ui_browse_paging_bar_invalid");
        await RequireLaptopResultDensityAsync(window);
        await ObserveCommandAsync(graph.Search.PageCommand, () => InvokeButton(Named<Button>(window, "LastPageButton")), ct);
        await PumpUiAsync(window);
        listing = graph.Search.Model.State;
        Require(listing.Rows.Length == 3 && listing.CurrentPage == 3 && !listing.HasNextPage && listing.HasPreviousPage
            && source.InteractiveSearchCalls == searchesBefore + 2, "ui_browse_last_page_missing");
        proof = proof with { BrowseAllPaged = true };

        _stage = "exact_search_keyboard";
        await ExpandFiltersAsync(window);
        SetText(window, "CodeField", StudentUiValidationSource.Key012.CourseCode);
        SetText(window, "SectionField", "012");
        var codeField = Named<TextBox>(window, "CodeField");
        // Exercise field traversal while the editor is open; Enter intentionally collapses short-layout filters.
        codeField.Focus(); Keyboard.Focus(codeField);
        var priorFocus = Keyboard.FocusedElement;
        var moved = codeField.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Require(moved && Keyboard.FocusedElement is DependencyObject focused && !ReferenceEquals(priorFocus, focused)
            && ReferenceEquals(Window.GetWindow(focused), window), "ui_tab_traversal_failed");
        await ObserveCommandAsync(graph.Search.SearchCommand,
            () => Require(SendEnter(codeField, window), "ui_enter_focus_unavailable"), ct);
        await PumpUiAsync(window);
        Require(graph.Search.Model.State.Rows.Length == 1
            && graph.Search.Model.State.Rows[0].Row.Key == StudentUiValidationSource.Key012, "ui_exact_012_lookup_failed");
        proof = proof with { LeadingZeroLookup = true, EnterSearch = true, TabTraversal = true };

        _stage = "details_fixed_actions";
        var results = Named<DataGrid>(window, "ResultsGrid");
        results.SetCurrentValue(Selector.SelectedItemProperty, graph.Search.Model.State.Rows[0]);
        await EventuallyAsync(() => graph.Search.Details.Model.State.Status == CourseUiLoadState.Complete
            && graph.Search.Details.Model.State.Key == StudentUiValidationSource.Key012, ct);
        Require(graph.Search.Details.Model.State.Read?.Data?.Capacity == 45
            && graph.Search.Model.State.Rows[0].Row.Capacity == 40, "ui_details_replaced_search_quota");
        await ShowSearchPaneAsync(window, details: true);
        Named<TabControl>(window, "SidePanel").SelectedIndex = 0;
        await PumpUiAsync(window);
        await InvokeCommandAsync(window, graph.Search.Details.OpenRegistrationCommand, ct);
        await InvokeCommandAsync(window, graph.Search.Details.OpenGenieCommand, ct);
        await InvokeCommandAsync(window, graph.Search.Details.CopyIdentifierCommand, ct);
        Require(actions.RegistrationIntents == 1 && actions.GenieIntents == 1 && actions.CopyIntents == 1, "ui_fixed_actions_failed");
        proof = proof with { DetailsQuotaSeparated = true, FixedActionIntents = true };
        captures.Add(await CaptureAsync(profile, window, "ui-search-en-normal.png", false, ct));
        await SaveLanguageAsync(window, graph, UiLanguage.Korean, ct);
        await NavigateAsync(window, graph, StudentPage.FindCourses);
        captures.Add(await CaptureAsync(profile, window, "ui-search-ko-normal.png", false, ct));
        captures.Add(await CaptureAsync(profile, window, "ui-search-ko-narrow.png", true, ct));
        // A narrow window leaves the term out of the header; the search card must still show it.
        Require(window.ActualWidth < NarrowWidth && !Visuals<TextBlock>(window).Single(block => block.Name == "TermLabel").IsVisible,
            "ui_narrow_header_term_shown");
        RequireShownTerm(window, "2026 - 겨울학기 (Demo)", "ui_narrow_term_missing");
        await SaveLanguageAsync(window, graph, UiLanguage.English, ct);
        await VerifyEmptyWatchColumnsAsync(window, graph, ct);
        await NavigateAsync(window, graph, StudentPage.FindCourses);

        _stage = "group_selection";
        await ExpandFiltersAsync(window);
        SetText(window, "SectionField", "");
        await InvokeCommandAsync(window, graph.Search.SearchCommand, ct);
        Require(graph.Search.Model.State.Rows.Length == 2, "ui_group_search_missing_rows");
        foreach (var key in new[] { StudentUiValidationSource.Key012, StudentUiValidationSource.Key013 })
        {
            results = Named<DataGrid>(window, "ResultsGrid");
            var item = graph.Search.Model.State.Rows.Single(row => row.Row.Key == key);
            results.ScrollIntoView(item); results.UpdateLayout();
            var row = results.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow
                ?? throw new InvalidOperationException("ui_result_row_not_realized");
            var check = Visuals<CheckBox>(row).FirstOrDefault(box => box.DataContext is CourseSearchItem)
                ?? throw new InvalidOperationException("ui_result_selection_missing");
            check.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, check));
            await PumpUiAsync(window);
        }
        await ShowSearchPaneAsync(window, details: true);
        Named<TabControl>(window, "SidePanel").SelectedIndex = 1;
        await PumpUiAsync(window);
        Require(graph.Search.WatchSelection.Model.State.Rows.Length == 2, "ui_explicit_group_selection_failed");
        SetText(window, "WatchLabelField", "Statistics choices (Demo)");
        // Confirm stays on screen without scrolling the form; the live preview.3 trial needed five page-downs to reach it.
        var confirm = Visuals<Button>(window).FirstOrDefault(button => button.IsVisible
            && ReferenceEquals(button.Command, graph.Search.WatchSelection.SaveCommand)) ?? throw new InvalidOperationException("ui_confirm_watch_missing");
        var confirmShown = VisibleBounds(confirm, window);
        Require(!confirmShown.IsEmpty && confirmShown.Height >= confirm.ActualHeight - 1, "ui_confirm_watch_offscreen");
        await InvokeCommandAsync(window, graph.Search.WatchSelection.SaveCommand, ct);
        var created = graph.Search.WatchSelection.Model.State.Created ?? throw new InvalidOperationException("ui_group_receipt_missing");
        Require(created.Kind == SubscriptionKind.Group && created.Members.Length == 2, "ui_group_receipt_invalid");
        await EventuallyAsync(() => graph.Shell.SelectedPage == StudentPage.MyWatches && graph.Watches.State.SelectedId == created.Id
            && graph.Watches.State.Details?.Members.Length == 2 && !graph.Watches.State.SelectionPending, ct);
        await EventuallyAsync(() => source.WatchSearchCalls >= 1
            && graph.Watches.State.Details?.Members.All(member => member.Health.DataState == SectionDataState.Current) == true, ct);
        Require(graph.Watches.State.Details!.Members.Single(member => member.Key == StudentUiValidationSource.Key012).Health.Availability == AvailabilityState.Full,
            "ui_full_member_not_current");
        proof = proof with { GroupWatch = true, AutoRoutedWatch = true, WatchId = created.Id };
        await PumpUiAsync(window);
        // A watch in the term the course page has read is listed under that term's name, never "Unconfirmed term".
        var unknownTerm = window.TryFindResource("Ui.TermUnknown") as string;
        Require(graph.Watches.State.Rows.Length > 0 && graph.Watches.State.Rows.All(row => row.TermLabel is not null)
            && !Visuals<TextBlock>(window).Any(block => block.Text == unknownTerm && IsRenderedTextVisible(block, window)),
            "ui_watch_term_unnamed");
        captures.Add(await CaptureAsync(profile, window, "ui-watches-en-normal.png", false, ct));
        // A compact reference capture selects the list; lifecycle actions belong to the details pane.
        await ShowCompactPaneAsync(window, "DetailsNavigation", required: false);

        _stage = "lifecycle";
        await InvokeCommandAsync(window, graph.Watches.PauseCommand, ct);
        Require(graph.Watches.State.Details!.Health.EffectiveWatchStatus == WatchStatus.Paused, "ui_pause_not_applied");
        await InvokeCommandAsync(window, graph.Watches.ResumeCommand, ct);
        Require(graph.Watches.State.Details!.Health.EffectiveWatchStatus == WatchStatus.Active, "ui_resume_not_applied");
        SetText(window, "RenameField", proof.Label);
        await InvokeCommandAsync(window, graph.Watches.RenameCommand, ct);
        Require(graph.Watches.State.Details!.Label == proof.Label, "ui_rename_not_saved");
        proof = proof with { PauseResume = true, Rename = true };

        _stage = "profile_save";
        await NavigateAsync(window, graph, StudentPage.Settings, ct);
        SetSetting(window, graph, "DesiredProfile", WatchMonitorProfile.Faster);
        await InvokeCommandAsync(window, graph.Settings.SaveCommand, ct);
        Require(graph.Settings.Model.State.Applied?.Profile == WatchMonitorProfile.Faster
            && graph.Settings.Model.State.Applied.PreferencesRevision == graph.Settings.Model.State.Stored?.Revision
            && !graph.Settings.Model.State.IsApplyPending, "ui_profile_not_applied");
        await VerifyLocalDiagnosticsUiAsync(window, graph, source, simulatedNative, ct);
        proof = proof with { ProfileApplied = true };
        captures.Add(await CaptureAsync(profile, window, "ui-settings-en-normal.png", false, ct));
        proof = await VerifySimulatedNativeUiAsync(profile, window, graph, simulatedNative, proof, captures, ct);
        _stage = "source_unavailable";
        await NavigateAsync(window, graph, StudentPage.FindCourses);
        source.FailInteractiveSearch = true;
        await InvokeCommandAsync(window, graph.Search.SearchCommand, ct);
        Require(graph.Search.Model.State.SearchState == CourseUiLoadState.Failed && graph.Search.Model.State.Rows.IsEmpty,
            "ui_source_failure_not_visible");
        proof = proof with { InteractiveUnavailable = true };

        _stage = "unknown_and_expiry";
        await SaveLanguageAsync(window, graph, UiLanguage.Korean, ct);
        await NavigateAsync(window, graph, StudentPage.MyWatches);
        await EventuallyAsync(() => graph.Watches.State.Details?.Source.Subscription.Id == created.Id && !graph.Watches.State.SelectionPending, ct);
        captures.Add(await CaptureAsync(profile, window, "ui-watches-ko-narrow.png", true, ct));
        InvokeButton(Named<Button>(window, "DetailsNavigation"));
        await PumpUiAsync(window);
        RequireVisibleWatchMember(window);
        await host.Session.SetOnlineAsync(false, ct);
        await EventuallyAsync(() => graph.Shell.State.Source.MessageKey == "Ui.Offline", ct);
        var callsBeforeAging = source.Calls;
        clock.AdvanceUtc(TimeSpan.FromMinutes(3)); graph.Shell.RefreshAges();
        var old = graph.Watches.State.Details!.Members.Single(member => member.Key == StudentUiValidationSource.Key012);
        Require(old.Health.Availability == AvailabilityState.Unknown && old.CountsText == "40/40" && source.Calls == callsBeforeAging,
            "ui_delayed_counts_claimed_full");
        proof = proof with { FullVsUnknown = true };
        var noExpiry = Visuals<CheckBox>(window).FirstOrDefault(box => box.IsVisible
            && box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path?.Path == "NoExpiry")
            ?? throw new InvalidOperationException("ui_expiry_checkbox_missing");
        noExpiry.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        noExpiry.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        var date = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), WatchExpiry.DefaultZone).Date;
        var expiry = Named<DatePicker>(window, "ExpiryField");
        expiry.SetCurrentValue(DatePicker.SelectedDateProperty, date);
        expiry.GetBindingExpression(DatePicker.SelectedDateProperty)?.UpdateSource();
        await InvokeCommandAsync(window, graph.Watches.SaveExpiryCommand, ct);
        Require(graph.Watches.State.Details!.Source.Subscription.Expiry is not null, "ui_expiry_not_saved");
        clock.AdvanceUtc(TimeSpan.FromDays(2)); graph.Shell.RefreshAges();
        Require(graph.Watches.State.Details!.Health.EffectiveWatchStatus == WatchStatus.Expired, "ui_expiry_not_projected");
        captures.Add(await CaptureAsync(profile, window, "ui-expired-ko.png", false, ct));
        proof = await VerifySavedAlertsUiAsync(profile, window, graph, source, proof, captures, ct);
        Require(graph.LastErrorCode is null && actions.NativeTestRequests == 0, "ui_unexpected_graph_or_native_action");
        return proof with { Expiry = true, PreferencesRevision = graph.Settings.Model.State.Stored!.Revision };
    }

    /// <summary>The stopping state must stay visible and keep Quit available, with every page action disabled.</summary>
    private static async Task ProbeTerminalStartAsync(StudentDesktopSession graph, StudentWindow primary)
    {
        using var terminal = new StudentWindow(graph.Shell, () => { }, enableTray: false)
            { ShowInTaskbar = false, ShowActivated = false };
        try
        {
            terminal.Show(); await PumpUiAsync(terminal);
            terminal.SetStopping(); graph.Shell.RefreshAges(); await PumpUiAsync(terminal);
            var status = terminal.FindName("LifecycleText") as TextBlock;
            var page = terminal.FindName("PageContent") as FrameworkElement;
            Require(status?.IsVisible == true && status.Text == terminal.TryFindResource("Ui.Stopping") as string
                && page?.IsEnabled == false && !Named<Button>(terminal, "KoreanLanguage").IsEnabled
                && !Named<RadioButton>(terminal, "FindNavigation").IsEnabled, "ui_terminal_start_hid_stopping");
            Require(Named<Button>(terminal, "QuitButton").IsEnabled, "ui_terminal_start_blocked_quit");
        }
        finally
        {
            // A closed auxiliary window must not leave a second live two-way filter binding behind.
            terminal.DataContext = null; terminal.Content = null;
            terminal.AllowClose(); terminal.Close(); primary.Activate();
        }
    }

    /// <summary>The search page reads the site's current term by itself, once; that read never runs a search.</summary>
    private static async Task EnsureCurrentTermAsync(StudentDesktopSession graph, StudentUiValidationSource source, CancellationToken ct)
    {
        await EventuallyAsync(() => graph.Search.Model.State.SelectedTerm is not null
            || graph.Search.Model.State.CatalogState == CourseUiLoadState.Failed, ct);
        var term = graph.Search.Model.State.SelectedTerm;
        Require(term is { Year: StudentUiValidationSource.Year, Selector: StudentUiValidationSource.Selector }
            && source.CatalogCalls >= 1, "ui_current_term_not_loaded");
    }

    private static async Task VerifyEmptyWatchColumnsAsync(StudentWindow window, StudentDesktopSession graph, CancellationToken ct)
    {
        _stage = "empty_watch_columns";
        // A first empty-list layout narrower than its columns (as at 200% on 1280x800) must keep fixed headers readable.
        window.Width = Math.Max(window.MinWidth, 700);
        window.Height = Math.Max(window.MinHeight, 620);
        await NavigateAsync(window, graph, StudentPage.MyWatches);
        await EventuallyAsync(() => graph.Watches.State.IsLoaded && !graph.Watches.State.IsLoading, ct);
        Require(graph.Watches.State.Rows.Length == 0, "ui_watch_list_not_empty");
        await PumpUiAsync(window);
        var grid = Named<DataGrid>(window, "WatchRows");
        Require(grid.Columns.Count == 4 && grid.Columns.All(column => column.ActualWidth + 0.5
            >= (column.Width.IsAbsolute ? column.Width.Value : column.MinWidth)), "ui_watch_columns_collapsed");
    }

    /// <summary>A common laptop work area in DIPs: 1280x800 at 100%, or 1920x1080 at 150% with the taskbar.</summary>
    private static readonly Size LaptopWorkArea = new(1280, 752);

    /// <summary>
    /// With a full result page loaded in a window filling a common laptop work area, at least three course rows must be
    /// fully visible at once. The live 0.1.0-preview.3 trial on such a screen showed one. Where the actual work area is
    /// smaller, the window cannot take this size and the small-work-area check covers reachability instead.
    /// </summary>
    private static async Task RequireLaptopResultDensityAsync(StudentWindow window)
    {
        var work = SystemParameters.WorkArea;
        if (work.Width < LaptopWorkArea.Width || work.Height < LaptopWorkArea.Height) return;
        var restore = new Rect(window.Left, window.Top, window.Width, window.Height);
        window.Left = work.Left; window.Top = work.Top;
        window.Width = LaptopWorkArea.Width; window.Height = LaptopWorkArea.Height;
        await PumpUiAsync(window);
        await ShowSearchPaneAsync(window, details: false);
        var grid = Named<DataGrid>(window, "ResultsGrid");
        grid.ScrollIntoView(grid.Items[0]); grid.UpdateLayout(); window.UpdateLayout();
        var fullyVisible = Visuals<DataGridRow>(grid).Count(row =>
        {
            var visible = VisibleBounds(row, window);
            return !visible.IsEmpty && visible.Height >= row.ActualHeight - 1;
        });
        window.Width = restore.Width; window.Height = restore.Height; window.Left = restore.Left; window.Top = restore.Top;
        await PumpUiAsync(window);
        Require(fullyVisible >= 3, "ui_laptop_result_rows_" + fullyVisible);
    }

    private static async Task ShowSearchPaneAsync(StudentWindow window, bool details)
    {
        var selection = Visuals<TabControl>(window).FirstOrDefault(control => control.IsVisible && control.Name == "CompactPaneSelector");
        if (selection is not null) selection.SelectedIndex = details ? 1 : 0;
        await PumpUiAsync(window);
    }

    private static async Task NavigateAsync(StudentWindow window, StudentDesktopSession graph, StudentPage page,
        CancellationToken ct = default)
    {
        var name = page switch
        {
            StudentPage.FindCourses => "FindNavigation", StudentPage.MyWatches => "WatchesNavigation",
            StudentPage.Alerts => "AlertsNavigation", StudentPage.Settings => "SettingsNavigation",
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
        Named<RadioButton>(window, name).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        await PumpUiAsync(window);
        Require(graph.Shell.SelectedPage == page, "ui_navigation_binding_failed");
        // Opening Settings reloads the saved values and briefly disables its buttons. Like a student, wait until they are
        // offered again: a click queued before the reload publishes "busy" would otherwise be ignored on a slow PC.
        if (page == StudentPage.Settings)
        {
            await EventuallyAsync(() => !graph.Settings.Model.State.IsBusy && !graph.Settings.ReloadCommand.IsExecuting, ct);
            await PumpUiAsync(window);
        }
    }

    private const double NarrowWidth = StudentWindow.NarrowWidth;

    /// <summary>
    /// The current term is always shown on the course page: in the search card's title, and also in the header when the
    /// window is wide enough. Windows caps the window to the screen, so at 200% on 1280x800 the window is always narrow.
    /// </summary>
    private static void RequireShownTerm(Window window, string expected, string code)
    {
        Require(Named<TextBlock>(window, "SearchTermText").Text == expected, code);
        var header = Visuals<TextBlock>(window).Single(block => block.Name == "TermLabel");
        if (window.ActualWidth >= NarrowWidth) Require(header.IsVisible && header.Text == expected, code);
    }

    private static async Task ClickLanguageAsync(StudentWindow window, StudentDesktopSession graph, UiLanguage language, CancellationToken ct)
    {
        InvokeButton(Named<Button>(window, language == UiLanguage.Korean ? "KoreanLanguage" : "EnglishLanguage"));
        await EventuallyAsync(() => graph.Settings.Model.State.Stored?.Values.Language == language && !graph.Settings.LanguageCommand.IsExecuting, ct);
        await PumpUiAsync(window);
    }

    private static async Task SaveLanguageAsync(StudentWindow window, StudentDesktopSession graph, UiLanguage language, CancellationToken ct)
    {
        await NavigateAsync(window, graph, StudentPage.Settings, ct);
        SetSetting(window, graph, "Language", language);
        await InvokeCommandAsync(window, graph.Settings.SaveCommand, ct);
        Require(graph.Settings.Model.State.Stored?.Values.Language == language, "ui_language_not_saved");
    }

    private static void SetSetting(StudentWindow window, StudentDesktopSession graph, string path, object value)
    {
        var field = Visuals<ComboBox>(window).Single(box => box.IsVisible && ReferenceEquals(box.DataContext, graph.Settings)
            && box.GetBindingExpression(Selector.SelectedValueProperty)?.ParentBinding.Path?.Path == path);
        field.SetCurrentValue(Selector.SelectedValueProperty, value);
        field.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateSource();
    }
}
