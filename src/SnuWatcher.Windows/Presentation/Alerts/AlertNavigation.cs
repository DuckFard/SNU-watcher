using System.Collections.Immutable;

namespace SnuWatcher.Windows.Presentation.Alerts;

internal enum AlertPageMove { Load, Reload, Latest, Newer, Older }

/// <summary>Exact cursors only; dropping backward positions never prevents continued forward paging.</summary>
internal sealed record AlertNavigation<T>(T? Cursor, T? Next, ImmutableArray<T?> Previous, long Number, bool Limited) where T : class
{
    internal static AlertNavigation<T> Empty { get; } = new(null, null, [], 1, false);
    internal T? Request(AlertPageMove move) => move switch
    {
        AlertPageMove.Load or AlertPageMove.Latest => null,
        AlertPageMove.Older => Next,
        AlertPageMove.Newer => Previous[^1],
        _ => Cursor,
    };
    internal AlertNavigation<T> Complete(AlertPageMove move, T? requested, T? next)
    {
        if (move is AlertPageMove.Load or AlertPageMove.Latest) return new(null, next, [], 1, false);
        if (move == AlertPageMove.Older)
        {
            var history = Previous.Add(Cursor);
            var limited = Limited || history.Length > 128;
            if (history.Length > 128) history = history.RemoveAt(0);
            return new(requested, next, history, checked(Number + 1), limited);
        }
        return move == AlertPageMove.Newer
            ? new(requested, next, Previous.RemoveAt(Previous.Length - 1), Number - 1, Limited)
            : this with { Next = next };
    }
}
