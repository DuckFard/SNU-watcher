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
            var name = peer.GetName() ?? "";
            Require(name.Length <= MaxAutomationName && !RecordDump().IsMatch(name), code);
            foreach (var child in peer.GetChildren() ?? []) pending.Push(child);
        }
    }

    [GeneratedRegex(@"\A\w+ \{ \w+ = ", RegexOptions.CultureInvariant)]
    private static partial Regex RecordDump();
}
