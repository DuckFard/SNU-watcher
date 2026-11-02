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
            Onboarding.Visibility = Visibility.Collapsed;
            FindNavigation.IsEnabled = WatchesNavigation.IsEnabled = AlertsNavigation.IsEnabled = SettingsNavigation.IsEnabled = false;
        }
        else
        {
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

    private void OnWindowLoaded(object sender, RoutedEventArgs args) => QueueWelcomeFocus();

    private void OnOnboardingVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => QueueWelcomeFocus();

    private void QueueWelcomeFocus()
    {
        // Wait for visibility bindings to enable the destination. Recheck the active window so
        // an auxiliary or closing window never takes focus from another application.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!IsLoaded || !IsVisible || !IsActive) return;
            UIElement target = Onboarding.Visibility == Visibility.Visible ? GetStartedButton
                : _finishing || _model is null ? QuitButton : FindNavigation;
            target.Focus();
        }));
    }

    internal void SetStopping()
    {
        Dispatcher.VerifyAccess();
        _finishing = true;
        Onboarding.Visibility = Visibility.Collapsed;
        PageContent.IsEnabled = false; Onboarding.IsEnabled = false;
        FindNavigation.IsEnabled = WatchesNavigation.IsEnabled = AlertsNavigation.IsEnabled = SettingsNavigation.IsEnabled = false;
        ShowStatusKey("Ui.Stopping");
    }

    private string Text(string key) => TryFindResource(key) as string ?? "SNU Watcher";

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (RootLayout is null || ChromeScrollViewer is null) return;
        var compact = ActualHeight < 560;
        RootLayout.Margin = new Thickness(compact ? 6 : 16);
        // Reserve usable page space; expanding source guidance cannot consume the result grid.
        ChromeScrollViewer.MaxHeight = compact ? Math.Max(56, RootLayout.ActualHeight - 250) : double.PositiveInfinity;
        HeaderPanel.Margin = new Thickness(0, 0, 0, compact ? 2 : 10);
        BrandingPanel.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        BrandingTitle.FontSize = compact ? 18 : 21;
        BrandingSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DemoLabel.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0, 2, 0, 0);
        DemoLabel.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Top;
        DemoLabel.FontSize = compact ? 11 : 12;
        QuitButton.MinHeight = compact ? 28 : 36;
        QuitButton.Padding = compact ? new Thickness(8, 3, 8, 3) : new Thickness(14, 9, 14, 9);
        QuitButton.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 8, 8);
        NavigationPanel.Margin = new Thickness(0, 0, 0, compact ? 2 : 8);
        SourceStatusExpander.Margin = new Thickness(0, 0, 0, compact ? 2 : 8);
        ReservationFooter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var navigation in new[] { FindNavigation, WatchesNavigation, AlertsNavigation, SettingsNavigation })
        {
            navigation.MinHeight = compact ? 28 : 36;
            navigation.Padding = compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 6, 10, 6);
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
