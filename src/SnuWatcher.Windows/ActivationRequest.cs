namespace SnuWatcher.Windows;

/// <summary>The closed set of operations accepted across the notification/IPC boundary.</summary>
public enum ActivationAction
{
    /// <summary>Bring the existing window to the foreground.</summary>
    Show,
    /// <summary>Open official registration in the default browser.</summary>
    OpenRegistration,
    /// <summary>Show the app's section/diagnostic view.</summary>
    ViewSection,
    /// <summary>Stop the app for a same-user installer/update request; never accepted from a toast.</summary>
    Quit
}

/// <summary>A validated action. No shell command, arbitrary URI, or serialized object is accepted.</summary>
public sealed record ActivationRequest
{
    /// <summary>Creates a request for a recognized action.</summary>
    public ActivationRequest(ActivationAction action)
    {
        if (!Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action));
        Action = action;
    }

    /// <summary>The recognized action.</summary>
    public ActivationAction Action { get; }

    /// <summary>Parses exact, length-bounded action arguments. Unknown or duplicated fields fail closed.</summary>
    public static ActivationRequest Parse(string? arguments) => arguments switch
    {
        "action=show" => new(ActivationAction.Show),
        "action=open-registration" => new(ActivationAction.OpenRegistration),
        "action=view-section" => new(ActivationAction.ViewSection),
        _ => throw new ArgumentException("Unrecognized activation arguments.", nameof(arguments))
    };

    /// <summary>Parses the same-user local transport; the installer may additionally request Quit.</summary>
    public static ActivationRequest ParseLocal(string? arguments) => arguments == "action=quit"
        ? new(ActivationAction.Quit)
        : Parse(arguments);

    /// <summary>Encodes the request for the same-user pipe and native toast arguments.</summary>
    public string Encode() => Action switch
    {
        ActivationAction.Show => "action=show",
        ActivationAction.OpenRegistration => "action=open-registration",
        ActivationAction.ViewSection => "action=view-section",
        ActivationAction.Quit => "action=quit",
        _ => throw new InvalidOperationException("Unrecognized action.")
    };

    /// <summary>Recognizes Windows App SDK's COM activation launch marker.</summary>
    public static bool IsNotificationLaunch(IEnumerable<string> arguments) => arguments.Any(
        value => value.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal));
}

/// <summary>Official fixed browser destinations; notification input never constructs a URL.</summary>
public static class OfficialDestinations
{
    /// <summary>Official SNU registration homepage.</summary>
    public static Uri Registration { get; } = new("https://sugang.snu.ac.kr/sugang/co/co010.action");

    /// <summary>Official SNU Genie homepage.</summary>
    public static Uri Genie { get; } = new("https://snugenie.snu.ac.kr/");
}
