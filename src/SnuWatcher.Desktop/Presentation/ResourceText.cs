using System;
using System.Windows;
using System.Windows.Controls;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>Binds a status resource key while retaining live language changes.</summary>
public static class ResourceText
{
    /// <summary>A fixed Ui.* string-resource key for a TextBlock.</summary>
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ResourceText), new FrameworkPropertyMetadata(string.Empty, OnKeyChanged));
    private static readonly DependencyProperty CollapsedEmptyKeyProperty = DependencyProperty.RegisterAttached(
        "CollapsedEmptyKey", typeof(bool), typeof(ResourceText), new PropertyMetadata(false));

    /// <summary>Read the localized status key.</summary>
    public static string? GetKey(DependencyObject target) => (string?)target.GetValue(KeyProperty);

    /// <summary>Set a presentation resource key; source messages are never accepted as markup.</summary>
    public static void SetKey(DependencyObject target, string? value) => target.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock text) throw new ArgumentException("ResourceText requires a TextBlock.");
        if (args.NewValue is not string key || key.Length == 0)
        {
            text.ClearValue(TextBlock.TextProperty);
            if (text.Visibility != Visibility.Collapsed)
            {
                text.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                text.SetValue(CollapsedEmptyKeyProperty, true);
            }
            return;
        }
        if (key.Length is < 4 or > 128 || !key.StartsWith("Ui.", StringComparison.Ordinal)
            || Array.Exists(key.ToCharArray(), value => !char.IsAsciiLetterOrDigit(value) && value != '.'))
            throw new ArgumentException("An explicit Ui.* resource key is required.");
        if ((bool)text.GetValue(CollapsedEmptyKeyProperty))
        {
            text.ClearValue(UIElement.VisibilityProperty);
            text.ClearValue(CollapsedEmptyKeyProperty);
        }
        text.SetResourceReference(TextBlock.TextProperty, key);
    }
}
