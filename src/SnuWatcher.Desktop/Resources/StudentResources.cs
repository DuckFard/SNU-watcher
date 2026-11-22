using System;
using System.Windows;
using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Desktop.Resources;

/// <summary>Replaces one window's localized resources from two fixed, compiled dictionaries.</summary>
internal sealed class StudentResources
{
    private readonly Window _window;
    private readonly ResourceDictionary _baseResources;

    internal StudentResources(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.Dispatcher.VerifyAccess();
        _baseResources = window.Resources;
        Apply(UiLanguage.English);
    }

    internal UiLanguage Language { get; private set; }

    internal static void InstallTheme(System.Windows.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Dispatcher.VerifyAccess();
        const string marker = "SnuWatcher.StudentThemeInstalled";
        if (application.Resources.Contains(marker)) return;
        var replacement = new ResourceDictionary();
        replacement.MergedDictionaries.Add(application.Resources);
        replacement.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/SnuWatcher.Desktop;component/Resources/StudentTheme.xaml", UriKind.Relative),
        });
        replacement.Add(marker, true);
        application.Resources = replacement;
    }

    internal void Apply(UiLanguage language)
    {
        _window.Dispatcher.VerifyAccess();
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        var file = language == UiLanguage.Korean ? "Strings.ko.xaml" : "Strings.en.xaml";
        var strings = new ResourceDictionary
        {
            Source = new Uri("/SnuWatcher.Desktop;component/Resources/" + file, UriKind.Relative),
        };
        // Build the replacement before publishing: a load failure leaves the current UI intact.
        var replacement = new ResourceDictionary();
        replacement.MergedDictionaries.Add(_baseResources);
        replacement.MergedDictionaries.Add(strings);
        _window.Resources = replacement;
        _window.Language = System.Windows.Markup.XmlLanguage.GetLanguage(language == UiLanguage.Korean ? "ko-KR" : "en-US");
        Language = language;
    }
}
