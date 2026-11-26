using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;

namespace SnuWatcher.Windows.Presentation.Watches;

/// <summary>
/// Names a watch's term from source-reported display data: the term the course page has just read, then the term
/// hints cached from earlier catalog reads of that year. Display only; a term with neither stays unnamed.
/// </summary>
public static class WatchTermLabels
{
    /// <summary>Returns the source's name for <paramref name="term"/>, in English when asked and available, or null.</summary>
    public static string? Find(AcademicTermIdentity term, CatalogTerm? current, TermHintCacheSnapshot? hints, bool english)
    {
        ArgumentNullException.ThrowIfNull(term);
        // A catalog term's selector is the section key's semester and detail-semester codes together.
        var selector = term.SemesterCode + term.DetailSemesterCode;
        if (current is not null && current.Year == term.Year && current.Selector == selector)
            return Pick(current.Name, current.EnglishName, english);
        var hint = hints?.Years.FirstOrDefault(year => year.Year == term.Year)?.Terms
            .FirstOrDefault(candidate => candidate.Selector == selector);
        return hint is null ? null : Pick(hint.Name, hint.EnglishName, english);
    }

    private static string Pick(string name, string? englishName, bool english) =>
        english && !string.IsNullOrWhiteSpace(englishName) ? englishName : name;
}
