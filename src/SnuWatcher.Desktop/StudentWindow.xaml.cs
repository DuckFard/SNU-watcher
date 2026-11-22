using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Desktop.Resources;
using SnuWatcher.Windows;
using SnuWatcher.Windows.Presentation.Shell;
using Forms = System.Windows.Forms;

namespace SnuWatcher.Desktop;

/// <summary>The student window owns only its visual/tray resources; the app owns runtime shutdown.</summary>
public partial class StudentWindow : Window, IDisposable
{
    private readonly StudentShellViewModel? _model;
    private readonly Action _requestQuit;
    private readonly StudentResources _resources;
    private readonly Forms.NotifyIcon? _tray;
    private bool _allowClose;
    private bool _finishing;
    private bool _syncingNavigation;
    private StudentPage? _shownPage;
    private bool _startupSeen;

    internal StudentWindow(StudentShellViewModel? model, Action requestQuit, bool enableTray = true)
    {
        StudentResources.InstallTheme(System.Windows.Application.Current);
        InitializeComponent();
        _requestQuit = requestQuit ?? throw new ArgumentNullException(nameof(requestQuit));
        _model = model;
        _resources = new(this);
        DataContext = model;
        var work = SystemParameters.WorkArea;
        var size = WindowSizePolicy.FitToWorkArea(work.Width, work.Height, Width, Height, MinWidth, MinHeight);
        MinWidth = size.MinimumWidth; MinHeight = size.MinimumHeight;
        Width = size.Width; Height = size.Height;
        Left = work.Left + (work.Width - Width) / 2; Top = work.Top + (work.Height - Height) / 2;
        if (model is null)
        {
            FindNavigation.IsEnabled = WatchesNavigation.IsEnabled = AlertsNavigation.IsEnabled = SettingsNavigation.IsEnabled = false;
            KoreanLanguage.IsEnabled = EnglishLanguage.IsEnabled = false;
        }
        else
        {
            _shownPage = model.SelectedPage;
            model.PropertyChanged += ModelChanged;
            model.PreferencesChanged += PreferencesChanged;
            _resources.Apply(model.State.Language);
        }
        if (enableTray)
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("SNU Watcher", null, (_, _) => ShowFromTray());
            menu.Items.Add(Text("Ui.Quit"), null, (_, _) => _requestQuit());
            _tray = new Forms.NotifyIcon
            {
                Text = "SNU Watcher", Icon = System.Drawing.SystemIcons.Information,
                ContextMenuStrip = menu, Visible = true,
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        Closing += OnClosing;
    }

    internal void ShowStatus(string message)
    {
        Dispatcher.VerifyAccess();
        if (_finishing) return; // Foreground/native-test notices cannot hide finalization guidance.
        LifecycleText.Text = message;
        LifecycleText.Visibility = Visibility.Visible;
    }

    internal void ShowStatusKey(string key)
    {
        Dispatcher.VerifyAccess();
        LifecycleText.SetResourceReference(TextBlock.TextProperty, key);
        LifecycleText.Visibility = Visibility.Visible;
        LifecycleText.BringIntoView();
    }

    internal void AllowClose() => _allowClose = true;

    private void OnWindowLoaded(object sender, RoutedEventArgs args) => QueueInitialFocus();

    private void QueueInitialFocus()
    {
        // There is no welcome screen: the course menu takes focus so the student can start right away. Recheck the
        // active window so an auxiliary or closing window never takes focus from another application.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!IsLoaded || !IsVisible || !IsActive) return;
            if (_finishing || _model is null) { QuitButton.Focus(); return; }
            // The course page opens with the caret in its search box; any other page starts on the menu.
            if (FindDescendant<Views.FindCoursesView>(PageContent) is not { } search || !search.FocusSearch()) FindNavigation.Focus();
        }));
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    internal void SetStopping()
    {
        Dispatcher.VerifyAccess();
        _finishing = true;
        PageContent.IsEnabled = false;
        FindNavigation.IsEnabled = WatchesNavigation.IsEnabled = AlertsNavigation.IsEnabled = SettingsNavigation.IsEnabled = false;
        KoreanLanguage.IsEnabled = EnglishLanguage.IsEnabled = false;
        ShowStatusKey("Ui.Stopping");
    }

    private string Text(string key) => TryFindResource(key) as string ?? "SNU Watcher";

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (RootLayout is null || ChromeScrollViewer is null) return;
        var compact = ActualHeight < 560;
        var narrow = ActualWidth < 860;
        var gutter = compact ? 8 : 20;
        // Reserve usable page space; expanding source guidance cannot consume the result grid.
        ChromeScrollViewer.MaxHeight = compact ? Math.Max(56, RootLayout.ActualHeight - 250) : double.PositiveInfinity;
        // Enough for a compact list pane's header, rows and paging; 150% on 1280x800 still fits without scrolling.
        PageContent.MinHeight = compact ? 340 : 0;
        PageScrollViewer.Margin = compact ? new Thickness(6, 2, 6, 2) : new Thickness(16, 8, 16, 6);
        HeaderBand.Padding = new Thickness(gutter, compact ? 1 : 8, compact ? 6 : gutter, 0);
        StatusBand.Padding = new Thickness(gutter, compact ? 0 : 4, gutter, 0);
        BrandMark.Width = BrandMark.Height = compact ? 24 : 36;
        BrandMark.CornerRadius = new CornerRadius(compact ? 12 : 18);
        BrandingPanel.Margin = new Thickness(0, compact ? 0 : 2, compact ? 12 : 24, compact ? 0 : 4);
        BrandingTitle.FontSize = compact || narrow ? 17 : 21;
        // Narrow windows leave the term to the search card, which always shows it, so the header never clips.
        TermLabel.FontSize = compact ? 15 : 18;
        TermLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        BrandingSubtitle.Visibility = compact || narrow ? Visibility.Collapsed : Visibility.Visible;
        DemoLabel.FontSize = compact ? 11 : 11.5;
        foreach (var link in new[] { KoreanLanguage, EnglishLanguage, QuitButton }) link.MinHeight = compact ? 20 : 24;
        FooterBand.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var navigation in new[] { FindNavigation, WatchesNavigation, AlertsNavigation, SettingsNavigation })
        {
            navigation.MinHeight = compact ? 30 : 42;
            navigation.FontSize = compact || narrow ? 14.5 : 16;
            navigation.Padding = compact ? new Thickness(2, 4, 2, 5) : new Thickness(2, 10, 2, 10);
            if (!ReferenceEquals(navigation, SettingsNavigation)) navigation.Margin = new Thickness(0, 0, compact || narrow ? 18 : 28, 0);
        }
    }

    private void OnNavigate(object sender, RoutedEventArgs args)
    {
        if (_syncingNavigation || DataContext is not StudentShellViewModel model
            || sender is not RadioButton { Tag: StudentPage page }) return;
        model.Navigate(page);
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_model is null) return;
        // A quick fade marks an actual page change; pages never slide (top-level transitions stay calm). The shell
        // re-announces its page with every status refresh (every two seconds), so compare with the page on screen.
        if (args.PropertyName == nameof(StudentShellViewModel.SelectedPage) && _model.SelectedPage != _shownPage)
        {
            _shownPage = _model.SelectedPage;
            Presentation.Motion.FadeInPage(PageContent);
        }
        // Local startup has finished: the course page may now read the site's current term.
        if (!_startupSeen && _model.State.IsLoaded)
        {
            _startupSeen = true;
            FindDescendant<Views.FindCoursesView>(PageContent)?.EnsureCurrentTerm();
        }
        _syncingNavigation = true;
        try
        {
            FindNavigation.IsChecked = _model.SelectedPage == StudentPage.FindCourses;
            WatchesNavigation.IsChecked = _model.SelectedPage == StudentPage.MyWatches;
            AlertsNavigation.IsChecked = _model.SelectedPage == StudentPage.Alerts;
            SettingsNavigation.IsChecked = _model.SelectedPage == StudentPage.Settings;
        }
        finally { _syncingNavigation = false; }
    }

    private void PreferencesChanged(UiPreferencesSnapshot snapshot)
    {
        Dispatcher.VerifyAccess();
        _resources.Apply(snapshot.Values.Language);
        if (_tray?.ContextMenuStrip is { } menu) menu.Items[1].Text = Text("Ui.Quit");
    }

    private void OnEnglish(object sender, RoutedEventArgs args) => SaveLanguage(UiLanguage.English);
    private void OnKorean(object sender, RoutedEventArgs args) => SaveLanguage(UiLanguage.Korean);

    private void SaveLanguage(UiLanguage language)
    {
        if (_model is null) return;
        try
        {
            if (!_model.Settings.SaveCommand.CanExecute(null)) { ShowStatusKey("Ui.SettingsPending"); return; }
            _model.Settings.Language = language; _model.Settings.SaveCommand.Execute(null);
        }
        catch (InvalidOperationException) { ShowStatusKey("Ui.SettingsSaveUncertain"); }
    }

    private void OnQuit(object sender, RoutedEventArgs args) => _requestQuit();

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (!_finishing && _tray is not null && _model?.State.CloseBehavior == UiCloseBehavior.MinimizeToTray) Hide();
        else _requestQuit();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void Dispose()
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= ModelChanged;
            _model.PreferencesChanged -= PreferencesChanged;
        }
        if (_tray is not null)
        {
            _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose();
        }
    }
}
