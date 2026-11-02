using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SnuWatcher.Core;
using SnuWatcher.Windows;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Navigation;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>Fixed browser destinations and dispatcher-owned clipboard access.</summary>
internal sealed class DesktopStudentActions(IUiDispatcher dispatcher) : IStudentActions
{
    private readonly IUiDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task OpenDestinationAsync(StudentDestination destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = destination switch
        {
            StudentDestination.Registration => OfficialDestinations.Registration,
            StudentDestination.Genie => OfficialDestinations.Genie,
            _ => throw new ArgumentOutOfRangeException(nameof(destination)),
        };
        // No course title, remote URL, or student input can become an executable or shell argument.
        using var process = Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task CopySectionIdentifierAsync(SectionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _dispatcher.InvokeAsync(() => System.Windows.Clipboard.SetText(key.CourseCode + " " + key.Section), cancellationToken);
    }
}
