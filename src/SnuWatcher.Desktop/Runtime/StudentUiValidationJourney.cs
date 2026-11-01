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
    public bool Onboarding { get; init; }
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
    public bool InvalidYearBlocked { get; init; }
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
        _stage = "onboarding";
        Require(graph.Shell.State.IsDemo && graph.Shell.State.ShowOnboarding, "ui_demo_onboarding_missing");
        await ProbeTerminalFirstRunAsync(graph, window);
        proof = proof with { TerminalStoppingVisible = true };
        captures.Add(await CaptureAsync(profile, window, "ui-onboarding-en.png", false, ct));
        await ClickLanguageAsync(window, graph, UiLanguage.Korean, ct);
        Require((window.TryFindResource("Ui.Settings") as string) == "설정", "ui_korean_resources_missing");
        await ClickLanguageAsync(window, graph, UiLanguage.English, ct);
        Require((window.TryFindResource("Ui.Settings") as string) == "Settings", "ui_english_resources_missing");
        await InvokeCommandAsync(window, graph.Shell.CompleteOnboardingCommand, ct);
        await EventuallyAsync(() => !graph.Shell.State.ShowOnboarding, ct);
        proof = proof with { Onboarding = true, EnglishKorean = true };

        _stage = "filters_validation";
        await ExpandFiltersAsync(window);
        SetText(window, "YearField", StudentUiValidationSource.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await LoadTermAsync(window, graph, ct);
        SetText(window, "CodeField", StudentUiValidationSource.Key012.CourseCode);
        var invalidYear = Named<TextBox>(window, "YearField");
        var searchesBefore = source.InteractiveSearchCalls;
        bindings.ExpectInvalidYear = true;
        try
        {
            SetText(window, "YearField", "abc");
            await PumpUiAsync(window);
            Require(Validation.GetHasError(invalidYear), "ui_invalid_year_not_validated");
            Require(!BoundButton(window, graph.Search.SearchCommand).IsEnabled
                && !BoundButton(window, graph.Search.LoadCatalogCommand).IsEnabled, "ui_invalid_year_buttons_enabled");
            _ = SendEnter(invalidYear, window);
            await PumpUiAsync(window); await Task.Delay(30, ct);
            Require(source.InteractiveSearchCalls == searchesBefore, "ui_invalid_year_queried_old_value");
            SetText(window, "YearField", StudentUiValidationSource.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await PumpUiAsync(window);
        }
        finally { bindings.ExpectInvalidYear = false; }
        Require(!Validation.GetHasError(invalidYear), "ui_year_correction_not_applied");
        proof = proof with { InvalidYearBlocked = true };
        _stage = "exact_search_keyboard";
        await LoadTermAsync(window, graph, ct);
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
        await SaveLanguageAsync(window, graph, UiLanguage.English, ct);
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
        captures.Add(await CaptureAsync(profile, window, "ui-watches-en-normal.png", false, ct));

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
        await NavigateAsync(window, graph, StudentPage.Settings);
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

    private static async Task ProbeTerminalFirstRunAsync(StudentDesktopSession graph, StudentWindow primary)
    {
        using var terminal = new StudentWindow(graph.Shell, () => { }, enableTray: false)
            { ShowInTaskbar = false, ShowActivated = false };
        try
        {
            terminal.Show(); await PumpUiAsync(terminal);
            terminal.SetStopping(); graph.Shell.RefreshAges(); await PumpUiAsync(terminal);
            var onboarding = terminal.FindName("Onboarding") as FrameworkElement;
            var status = terminal.FindName("LifecycleText") as TextBlock;
            var page = terminal.FindName("PageContent") as FrameworkElement;
            Require(graph.Shell.State.ShowOnboarding && onboarding?.Visibility == Visibility.Collapsed
                && status?.IsVisible == true && status.Text == terminal.TryFindResource("Ui.Stopping") as string
                && page?.IsEnabled == false, "ui_terminal_onboarding_hid_stopping");
        }
        finally
        {
            // A closed auxiliary window must not leave a second live two-way filter binding behind.
            terminal.DataContext = null; terminal.Content = null;
            terminal.AllowClose(); terminal.Close(); primary.Activate();
        }
    }

    private static async Task LoadTermAsync(StudentWindow window, StudentDesktopSession graph, CancellationToken ct)
    {
        await InvokeCommandAsync(window, graph.Search.LoadCatalogCommand, ct);
        Require(graph.Search.Model.State.CatalogState == CourseUiLoadState.Complete, "ui_catalog_not_loaded");
        var term = graph.Search.Model.State.Terms.Single(option => option.Selector == StudentUiValidationSource.Selector);
        var selection = Named<ComboBox>(window, "TermField");
        selection.SetCurrentValue(Selector.SelectedItemProperty, term);
        selection.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateSource();
        Require(graph.Search.SelectedTerm == term, "ui_term_binding_not_applied");
    }

    private static async Task ShowSearchPaneAsync(StudentWindow window, bool details)
    {
        var selection = Visuals<TabControl>(window).FirstOrDefault(control => control.IsVisible && control.Name == "CompactPaneSelector");
        if (selection is not null) selection.SelectedIndex = details ? 1 : 0;
        await PumpUiAsync(window);
    }

    private static async Task NavigateAsync(StudentWindow window, StudentDesktopSession graph, StudentPage page)
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
    }

    private static async Task ClickLanguageAsync(StudentWindow window, StudentDesktopSession graph, UiLanguage language, CancellationToken ct)
    {
        var text = language == UiLanguage.Korean ? "한국어" : "English";
        InvokeButton(Visuals<Button>(window).Single(button => button.IsVisible && button.Content is string content && content == text));
        await EventuallyAsync(() => graph.Settings.Model.State.Stored?.Values.Language == language && !graph.Settings.SaveCommand.IsExecuting, ct);
        await PumpUiAsync(window);
    }

    private static async Task SaveLanguageAsync(StudentWindow window, StudentDesktopSession graph, UiLanguage language, CancellationToken ct)
    {
        await NavigateAsync(window, graph, StudentPage.Settings);
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
