using System.Windows;
using System.Windows.Media;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>
/// State colors a shared control template reads from its styled parent, so one button template serves every variant
/// (primary, secondary, navy-panel pills). Values are normally palette brushes set with DynamicResource.
/// </summary>
public static class Paint
{
    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(Brush), typeof(Paint), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached(
        "Pressed", typeof(Brush), typeof(Paint), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty HoverBorderProperty = DependencyProperty.RegisterAttached(
        "HoverBorder", typeof(Brush), typeof(Paint), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty FocusProperty = DependencyProperty.RegisterAttached(
        "Focus", typeof(Brush), typeof(Paint), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Paint), new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static Brush? GetHover(DependencyObject target) => (Brush?)target.GetValue(HoverProperty);
    public static void SetHover(DependencyObject target, Brush? value) => target.SetValue(HoverProperty, value);
    public static Brush? GetPressed(DependencyObject target) => (Brush?)target.GetValue(PressedProperty);
    public static void SetPressed(DependencyObject target, Brush? value) => target.SetValue(PressedProperty, value);
    public static Brush? GetHoverBorder(DependencyObject target) => (Brush?)target.GetValue(HoverBorderProperty);
    public static void SetHoverBorder(DependencyObject target, Brush? value) => target.SetValue(HoverBorderProperty, value);
    public static Brush? GetFocus(DependencyObject target) => (Brush?)target.GetValue(FocusProperty);
    public static void SetFocus(DependencyObject target, Brush? value) => target.SetValue(FocusProperty, value);
    public static CornerRadius GetRadius(DependencyObject target) => (CornerRadius)target.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject target, CornerRadius value) => target.SetValue(RadiusProperty, value);
}
