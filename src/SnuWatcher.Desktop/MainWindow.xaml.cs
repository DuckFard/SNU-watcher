using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using SnuWatcher.Windows;
using Forms = System.Windows.Forms;

namespace SnuWatcher.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private readonly Action _sendTest;
    private readonly Action _quit;
    private readonly Forms.NotifyIcon _tray;
    private bool _allowClose;

    public MainWindow(Action sendTest, Action quit)
    {
        InitializeComponent();
        _sendTest = sendTest;
        _quit = quit;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open SNU Watcher", null, (_, _) => ShowFromTray());
        menu.Items.Add("Send test notification", null, (_, _) => _sendTest());
        menu.Items.Add("Quit", null, (_, _) => _quit());
        _tray = new Forms.NotifyIcon
        {
            Text = "SNU Watcher - native feasibility",
            Icon = System.Drawing.SystemIcons.Information,
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        Closing += OnClosing;
    }

    public void SetStatus(string message) => StatusText.Text = message;
    public void AllowClose() => _allowClose = true;

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose || Application.Current.ShutdownMode == ShutdownMode.OnMainWindowClose) return;
        args.Cancel = true;
        Hide();
    }

    private void OnTestNotification(object sender, RoutedEventArgs args) => _sendTest();
    private void OnQuit(object sender, RoutedEventArgs args) => _quit();
    private void OnOpenSnu(object sender, RoutedEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo(OfficialDestinations.Registration.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { SetStatus("The default browser could not be opened."); }
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
    }
}
