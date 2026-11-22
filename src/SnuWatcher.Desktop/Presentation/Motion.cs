using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>
/// Small state transitions driven by bindings (for example <c>IsMouseOver</c>). Each property animates the element's
/// own opacity or render transform, so palette brushes stay dynamic for Windows high contrast. Durations follow short
/// ease-out timings; when Windows "Animation effects" is off, or motion is suppressed for UI validation, every
/// transition jumps straight to its end state. Motion is decoration only: no state is conveyed by animation alone.
/// </summary>
public static class Motion
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan NudgeDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TurnDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PageDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>Set by validation runs so captures always show final states.</summary>
    internal static bool Suppressed { get; set; }

    private static bool Animate => !Suppressed && SystemParameters.ClientAreaAnimation;

    /// <summary>Fades the element in (true) or out (false).</summary>
    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInChanged));
    /// <summary>Grows the element horizontally from its render origin (true) or collapses it (false).</summary>
    public static readonly DependencyProperty ExpandXProperty = DependencyProperty.RegisterAttached(
        "ExpandX", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnExpandXChanged));
    /// <summary>Grows the element vertically from its render origin (true) or collapses it (false).</summary>
    public static readonly DependencyProperty ExpandYProperty = DependencyProperty.RegisterAttached(
        "ExpandY", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnExpandYChanged));
    /// <summary>Moves the element 3 device-independent pixels to the right (true) or back (false).</summary>
    public static readonly DependencyProperty NudgeRightProperty = DependencyProperty.RegisterAttached(
        "NudgeRight", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnNudgeChanged));
    /// <summary>Turns the element a quarter turn (true) or back (false) around its render origin.</summary>
    public static readonly DependencyProperty TurnProperty = DependencyProperty.RegisterAttached(
        "Turn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTurnChanged));

    public static bool GetFadeIn(DependencyObject target) => (bool)target.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject target, bool value) => target.SetValue(FadeInProperty, value);
    public static bool GetExpandX(DependencyObject target) => (bool)target.GetValue(ExpandXProperty);
    public static void SetExpandX(DependencyObject target, bool value) => target.SetValue(ExpandXProperty, value);
    public static bool GetExpandY(DependencyObject target) => (bool)target.GetValue(ExpandYProperty);
    public static void SetExpandY(DependencyObject target, bool value) => target.SetValue(ExpandYProperty, value);
    public static bool GetNudgeRight(DependencyObject target) => (bool)target.GetValue(NudgeRightProperty);
    public static void SetNudgeRight(DependencyObject target, bool value) => target.SetValue(NudgeRightProperty, value);
    public static bool GetTurn(DependencyObject target) => (bool)target.GetValue(TurnProperty);
    public static void SetTurn(DependencyObject target, bool value) => target.SetValue(TurnProperty, value);

    /// <summary>Quick page-level fade used when the student switches pages (no sliding, per top-level guidance).</summary>
    internal static void FadeInPage(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Animate) { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; return; }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1, PageDuration) { EasingFunction = EaseOut });
    }

    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Freeze(CubicEase ease) { ease.Freeze(); return ease; }

    private static void OnFadeInChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        Run(element, UIElement.OpacityProperty, (bool)args.NewValue ? 1 : 0, FadeDuration);
    }

    private static void OnExpandXChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        Run(Ensure<ScaleTransform>(element), ScaleTransform.ScaleXProperty, (bool)args.NewValue ? 1 : 0, ExpandDuration);
    }

    private static void OnExpandYChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        Run(Ensure<ScaleTransform>(element), ScaleTransform.ScaleYProperty, (bool)args.NewValue ? 1 : 0, ExpandDuration);
    }

    private static void OnNudgeChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        Run(Ensure<TranslateTransform>(element), TranslateTransform.XProperty, (bool)args.NewValue ? 3 : 0, NudgeDuration);
    }

    private static void OnTurnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        Run(Ensure<RotateTransform>(element), RotateTransform.AngleProperty, (bool)args.NewValue ? 90 : 0, TurnDuration);
    }

    private static void Run(IAnimatable target, DependencyProperty property, double to, TimeSpan duration)
    {
        if (!Animate)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        target.BeginAnimation(property, new DoubleAnimation(to, duration) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Gives the element its own animatable transform. Templates declare the resting state (for example a collapsed
    /// underline with ScaleX 0); those shared transforms are frozen, so they are cloned with their current values.
    /// </summary>
    private static T Ensure<T>(UIElement element) where T : Transform, new()
    {
        if (element.RenderTransform is T existing)
        {
            if (!existing.IsFrozen) return existing;
            var copy = (T)existing.Clone();
            element.RenderTransform = copy;
            return copy;
        }
        var created = new T();
        element.RenderTransform = created;
        return created;
    }
}

/// <summary>True when any bound value is true; used to drive one motion from several states (hover or selected).</summary>
public sealed class AnyTrueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        Array.Exists(values, value => value is true);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
