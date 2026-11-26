using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;

namespace SnuWatcher.Desktop.Runtime;

internal static partial class StudentUiValidation
{
    /// <summary>The longest accessible name the installed-app UI Automation check accepts.</summary>
    private const int MaxAutomationName = 256;
    private const int MaxAutomationPeers = 5000;

    /// <summary>
    /// Every name a screen reader can announce stays short and readable. Without an explicit name, WPF names a grid row
    /// after its item's ToString(), which for a C# record is a dump such as "WatchRow { Definition = ... }".
    /// </summary>
    private static void RequireReadableAutomationNames(Window window, string code)
    {
        var root = UIElementAutomationPeer.CreatePeerForElement(window) ?? throw new InvalidOperationException("ui_window_peer_missing");
        var pending = new Stack<AutomationPeer>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0)
        {
            var peer = pending.Pop();
            if (++visited > MaxAutomationPeers) throw new InvalidOperationException("ui_automation_tree_too_large");
            // WPF's own item peer throws for an item whose row was virtualized away in a small window; such an item has
            // no element on screen, so there is nothing to name until it scrolls into view.
            if (!TryRead(peer.GetName, out var name)) continue;
            Require((name ?? "").Length <= MaxAutomationName && !RecordDump().IsMatch(name ?? ""), code);
            if (TryRead(peer.GetChildren, out var children)) foreach (var child in children ?? []) pending.Push(child);
        }
    }

    private static bool TryRead<T>(Func<T> read, out T? value)
    {
        try { value = read(); return true; }
        catch (Exception error) when (error is NullReferenceException or InvalidOperationException
            or System.Windows.Automation.ElementNotAvailableException)
        { value = default; return false; }
    }

    [GeneratedRegex(@"\A\w+ \{ \w+ = ", RegexOptions.CultureInvariant)]
    private static partial Regex RecordDump();
}
