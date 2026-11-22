using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using SnuWatcher.Application.Sources;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>
/// Shows a source term the way the registration site does beside its logo ("2026 - 겨울학기"), or with the source's
/// English name ("2026 Winter Semester") when the window is in English. The second binding is the element's
/// language, so the label follows a language switch. An unknown term shows nothing.
/// </summary>
public sealed class TermLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not CatalogTerm term) return "";
        var english = values.Length > 1 && values[1] is XmlLanguage language
            && language.IetfLanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        return english && !string.IsNullOrWhiteSpace(term.EnglishName)
            ? string.Create(CultureInfo.InvariantCulture, $"{term.Year} {term.EnglishName}")
            : string.Create(CultureInfo.InvariantCulture, $"{term.Year} - {term.Name}");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
