using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Presentation.Watches;
using SnuWatcher.Windows.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Settings;

namespace SnuWatcher.Desktop.Runtime;

internal sealed record StudentUiCapture(string FileName, int Width, int Height, double ExportDpi,
    double ActualOsDpiX, double ActualOsDpiY, string Sha256);

internal static partial class StudentUiValidation
{
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement =>
        Visuals<T>(window).SingleOrDefault(element => element.Name == name && element.IsVisible)
        ?? throw new InvalidOperationException("ui_missing_" + name);

    private static Button BoundButton(Window window, UiAsyncCommand command) =>
        Visuals<Button>(window).FirstOrDefault(button => button.IsVisible && ReferenceEquals(button.Command, command))
        ?? throw new InvalidOperationException("ui_bound_button_missing");

    private static Task PumpUiAsync(Window window) => window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle).Task;

    private static async Task EventuallyAsync(Func<bool> completed, CancellationToken ct)
    {
        while (!completed()) { ct.ThrowIfCancellationRequested(); await Task.Delay(20, ct); }
    }

    private static void Require(bool condition, string code)
    { if (!condition) throw new InvalidOperationException(code); }

    private static void SetText(Window window, string name, string value)
    {
        var field = Named<TextBox>(window, name);
        field.SetCurrentValue(TextBox.TextProperty, value);
        field.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private static void InvokeButton(Button button)
    {
        Require(button.IsVisible && button.IsEnabled, "ui_button_unavailable");
        button.BringIntoView();
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("ui_button_invoke_unavailable");
        invoke.Invoke(); // In-process WPF invoke; no operating-system input is generated.
    }

    private static async Task InvokeCommandAsync(Window window, UiAsyncCommand command, CancellationToken ct)
    {
        await EventuallyAsync(() => !command.IsExecuting && command.CanExecute(null), ct);
        await ObserveCommandAsync(command, () => InvokeButton(BoundButton(window, command)), ct);
        await PumpUiAsync(window);
    }

    private static async Task ObserveCommandAsync(UiAsyncCommand command, Action invoke, CancellationToken ct)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(UiAsyncCommand.IsExecuting)) return;
            if (command.IsExecuting) started = true;
            else if (started) complete.TrySetResult();
        }
        command.PropertyChanged += Changed;
        try { invoke(); await complete.Task.WaitAsync(ct); }
        finally { command.PropertyChanged -= Changed; }
    }

    private static bool SendEnter(TextBox field, Window window)
    {
        field.BringIntoView();
        field.Focus(); Keyboard.Focus(field);
        if (!ReferenceEquals(Keyboard.FocusedElement, field)) return false;
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("ui_input_source_missing");
        field.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Return)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        return true;
    }

    private static async Task<StudentUiCapture> CaptureAsync(RuntimeProfile profile, StudentWindow window,
        string leaf, bool narrow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Require(leaf is "ui-onboarding-en.png" or "ui-search-en-normal.png" or "ui-search-ko-normal.png"
            or "ui-search-ko-narrow.png" or "ui-watches-en-normal.png" or "ui-watches-ko-narrow.png"
            or "ui-settings-en-normal.png" or "ui-expired-ko.png" or "ui-simulated-native-en.png"
            or "ui-simulated-native-ko-narrow.png" or "ui-alerts-en-normal.png" or "ui-alerts-ko-narrow.png"
            or "ui-alerts-ko-details-narrow.png", "ui_capture_name_invalid");
        window.Width = narrow ? Math.Max(window.MinWidth, 700) : 1180;
        window.Height = narrow ? Math.Max(window.MinHeight, 620) : 820;
        foreach (var scroll in Visuals<ScrollViewer>(window)) scroll.ScrollToTop();
        await PumpUiAsync(window);
        if (leaf.StartsWith("ui-simulated-native-", StringComparison.Ordinal))
        {
            var confirmation = Visuals<Button>(window).First(button => button.IsVisible
                && button.CommandParameter is NativeTestBannerConfirmation.NotSeen);
            await RequireReachableAsync(confirmation, window, "ui_simulated_confirmation_unreachable");
        }
        if (leaf.StartsWith("ui-alerts-", StringComparison.Ordinal))
        {
            if (narrow)
            {
                InvokeButton(Named<Button>(window, leaf.Contains("details", StringComparison.Ordinal) ? "DetailsNavigation" : "ListNavigation"));
                await PumpUiAsync(window);
            }
            if (!leaf.Contains("details", StringComparison.Ordinal))
            {
                var history = Named<DataGrid>(window, "HistoryRows");
                RequireVisibleRow(history, window, item => item is AlertHistoryRow row
                    && row.Source.Trigger.Key == StudentUiValidationSource.Key013, "ui_alert_rows_not_visible", requireIdentifier: false);
                Require(Visuals<TextBlock>(history).Any(block => block.Text == "F32.102 013" && IsRenderedTextVisible(block, window)),
                    "ui_alert_identifier_not_visible");
            }
        }
        if (leaf.StartsWith("ui-search-", StringComparison.Ordinal))
        {
            var panes = Visuals<TabControl>(window).FirstOrDefault(control => control.IsVisible && control.Name == "CompactPaneSelector");
            if (panes is not null) { panes.SelectedIndex = 0; await PumpUiAsync(window); }
            RequireVisibleRow(Named<DataGrid>(window, "ResultsGrid"), window,
                item => item is CourseSearchItem course && course.Row.Key == StudentUiValidationSource.Key012,
                "ui_search_rows_not_visible", requireIdentifier: true);
        }
        if (leaf.StartsWith("ui-watches-", StringComparison.Ordinal))
        {
            if (narrow)
            {
                InvokeButton(Named<Button>(window, "ListNavigation"));
                await PumpUiAsync(window);
            }
            var grid = Visuals<DataGrid>(window).FirstOrDefault(candidate => candidate.IsVisible && candidate.Items.OfType<WatchRow>().Any())
                ?? throw new InvalidOperationException("ui_watch_list_missing");
            RequireVisibleRow(grid, window, item => item is WatchRow, "ui_watch_rows_not_visible", requireIdentifier: false);
            if (!narrow) RequireVisibleWatchMember(window);
        }
        return WriteCapture(profile, window, leaf);
    }

    private static async Task<StudentUiCapture> CaptureActualAsync(RuntimeProfile profile, StudentWindow window,
        string leaf, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Require(leaf is "ui-actual-search.png" or "ui-actual-watches-list.png"
            or "ui-actual-watches-details.png" or "ui-actual-settings.png", "ui_actual_capture_name_invalid");
        // The OS-sized journey has already made the current controls reachable. Do not resize or reset its scroll position.
        await PumpUiAsync(window);
        return WriteCapture(profile, window, leaf);
    }

    private static StudentUiCapture WriteCapture(RuntimeProfile profile, StudentWindow window, string leaf)
    {
        // Window.ActualWidth includes native frame space that RenderTargetBitmap cannot paint.
        // Capture only this window's painted WPF client bounds, rather than transparent frame padding.
        var content = ClientBounds(window);
        var width = checked((int)Math.Ceiling(content.Right));
        var height = checked((int)Math.Ceiling(content.Bottom));
        Require(width is >= 320 and <= 2000 && height is >= 240 and <= 1600, "ui_capture_bounds_invalid");
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        profile.ValidateExisting();
        var path = Path.Combine(profile.DirectoryPath, leaf);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.WriteThrough))
        { encoder.Save(stream); stream.Flush(flushToDisk: true); }
        Require(new FileInfo(path).Length is > 0 and <= 16 * 1024 * 1024, "ui_capture_file_limit");
        using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(leaf, width, height, 96, dpi.PixelsPerInchX, dpi.PixelsPerInchY, Convert.ToHexString(SHA256.HashData(image)));
    }

    private static void RequireVisibleWatchMember(Window window)
    {
        var grid = Visuals<DataGrid>(window).FirstOrDefault(candidate => candidate.IsVisible
            && candidate.Items.OfType<WatchMemberRow>().Any(member => member.Key == StudentUiValidationSource.Key012))
            ?? throw new InvalidOperationException("ui_watch_member_grid_missing");
        grid.BringIntoView();
        RequireVisibleRow(grid, window, item => item is WatchMemberRow member && member.Key == StudentUiValidationSource.Key012,
            "ui_watch_member_not_visible", requireIdentifier: true);
    }

    private static void RequireVisibleRow(DataGrid grid, Window window, Func<object, bool> matching,
        string code, bool requireIdentifier)
    {
        var item = grid.Items.Cast<object>().FirstOrDefault(matching) ?? throw new InvalidOperationException(code);
        grid.ScrollIntoView(item); grid.UpdateLayout(); window.UpdateLayout();
        var row = Visuals<DataGridRow>(grid).FirstOrDefault(candidate => ReferenceEquals(candidate.Item, item))
            ?? throw new InvalidOperationException(code);
        var visible = VisibleBounds(row, window);
        foreach (var header in Visuals<DataGridColumnHeadersPresenter>(grid).Where(header => header.IsVisible))
        {
            var bounds = header.TransformToAncestor(window).TransformBounds(new Rect(header.RenderSize));
            if (visible.IntersectsWith(bounds) && visible.Top < bounds.Bottom)
                visible = visible.Bottom <= bounds.Bottom ? Rect.Empty : new Rect(visible.Left, bounds.Bottom, visible.Width, visible.Bottom - bounds.Bottom);
        }
        Require(!visible.IsEmpty && visible.Height >= 24 && visible.Width >= 100, code);
        if (!requireIdentifier) return;
        var text = Visuals<TextBlock>(row).Where(block => IsRenderedTextVisible(block, window))
            .Select(block => block.Text ?? "");
        var rendered = string.Join(" ", text);
        Require(rendered.Contains(StudentUiValidationSource.Key012.CourseCode, StringComparison.Ordinal)
            && rendered.Contains("012", StringComparison.Ordinal), code + "_identifier");
    }

    private static Rect VisibleBounds(FrameworkElement element, Window window)
    {
        var visible = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is UIElement clip && (clip.ClipToBounds || clip is ScrollContentPresenter or ScrollViewer or DataGrid || clip.Clip is not null))
            {
                var local = clip.Clip?.Bounds ?? new Rect(clip.RenderSize);
                visible.Intersect(clip.TransformToAncestor(window).TransformBounds(local));
            }
            if (ReferenceEquals(ancestor, window)) break;
        }
        visible.Intersect(ClientBounds(window));
        return visible;
    }

    private static bool IsRenderedTextVisible(TextBlock block, Window window)
    {
        if (!block.IsVisible || string.IsNullOrEmpty(block.Text)) return false;
        var visible = VisibleBounds(block, window);
        if (visible.IsEmpty || visible.Height < 12) return false;
        var text = new FormattedText(block.Text, CultureInfo.CurrentUICulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
        var available = block.ActualWidth - block.Padding.Left - block.Padding.Right;
        if (available < text.WidthIncludingTrailingWhitespace) return false;
        text.MaxTextWidth = available;
        text.TextAlignment = block.TextAlignment;
        var glyphs = text.BuildGeometry(new Point(block.Padding.Left, block.Padding.Top)).Bounds;
        if (glyphs.IsEmpty) return false;
        var required = block.TransformToAncestor(window).TransformBounds(glyphs);
        return visible.Contains(required);
    }

    private static async Task ProbeGlyphGuardsAsync(StudentWindow primary)
    {
        var viewport = new Canvas { Width = 220, Height = 80, ClipToBounds = true, Background = Brushes.White };
        var probe = new Window { Width = 300, Height = 170, Content = viewport, Background = Brushes.White,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow };
        try
        {
            probe.Show();
            var text = new TextBlock { Text = "F32.102 012", FontSize = 14, Width = 200, Height = 32 };
            viewport.Children.Add(text); Canvas.SetLeft(text, 10); Canvas.SetTop(text, 10);
            await PumpUiAsync(probe);
            Require(IsRenderedTextVisible(text, probe), "ui_glyph_guard_rejected_visible_identifier");
            Canvas.SetLeft(text, -20); await PumpUiAsync(probe);
            Require(!IsRenderedTextVisible(text, probe), "ui_glyph_guard_accepted_left_clipping");
            Canvas.SetLeft(text, 10); text.Width = 10; text.ClipToBounds = true; await PumpUiAsync(probe);
            Require(!IsRenderedTextVisible(text, probe), "ui_glyph_guard_accepted_narrow_box");
            text.Width = 250; text.TextAlignment = TextAlignment.Right; await PumpUiAsync(probe);
            Require(!IsRenderedTextVisible(text, probe), "ui_glyph_guard_accepted_right_clipping");
            text.Width = 200; text.FlowDirection = FlowDirection.RightToLeft; await PumpUiAsync(probe);
            Require(IsRenderedTextVisible(text, probe), "ui_glyph_guard_rejected_visible_rtl");
        }
        finally { probe.Close(); primary.Activate(); }
    }

    private static Rect ClientBounds(Window window)
    {
        var painted = VisualTreeHelper.GetContentBounds(window);
        if (!painted.IsEmpty && painted.Width > 0 && painted.Height > 0) return painted;
        if (VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement client)
            return client.TransformToAncestor(window).TransformBounds(new Rect(client.RenderSize));
        return new Rect(window.RenderSize);
    }
}

/// <summary>Bounded private binding evidence. Only the deliberately invalid year conversion is an expected validation error.</summary>
internal sealed class StudentUiBindingProbe : TraceListener
{
    private readonly object _sync = new();
    private readonly SourceLevels _previousLevel;
    private ImmutableArray<string> _errors = [];
    private int _errorCount, _expectedCount, _expectYear;
    internal StudentUiBindingProbe()
    {
        _previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(this);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }
    internal int ErrorCount => Volatile.Read(ref _errorCount);
    internal int ExpectedValidationCount => Volatile.Read(ref _expectedCount);
    internal ImmutableArray<string> Errors { get { lock (_sync) return _errors; } }
    internal bool ExpectInvalidYear { set => Volatile.Write(ref _expectYear, value ? 1 : 0); }
    public override bool IsThreadSafe => true;
    public override void Write(string? message) { }
    public override void WriteLine(string? message) { }
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (eventType is not (TraceEventType.Error or TraceEventType.Critical)) return;
        var text = message ?? "binding_error";
        if (Volatile.Read(ref _expectYear) != 0 && text.Contains("abc", StringComparison.Ordinal)
            && (text.Contains("Format", StringComparison.OrdinalIgnoreCase) || text.Contains("Convert", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Int32", StringComparison.Ordinal)))
        { Interlocked.Increment(ref _expectedCount); return; }
        Interlocked.Increment(ref _errorCount);
        text = new string(text.Take(600).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        try { text = Regex.Replace(text, @"https?://\S+|[A-Za-z]:\\\S+", "[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (RegexMatchTimeoutException) { text = "binding_error"; }
        lock (_sync) if (_errors.Length < 16) _errors = _errors.Add(id.ToString(CultureInfo.InvariantCulture) + ": " + text);
    }
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
        string? format, params object?[]? args) => TraceEvent(eventCache, source, eventType, id,
            args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, format ?? "", args) : format);
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = _previousLevel;
        }
        base.Dispose(disposing);
    }
}
