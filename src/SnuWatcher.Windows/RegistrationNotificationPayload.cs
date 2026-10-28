using System.Xml.Linq;

namespace SnuWatcher.Windows;

/// <summary>Builds registration notifications whose clicks use Windows HTTPS activation, without an app callback.</summary>
public static class RegistrationNotificationPayload
{
    /// <summary>Creates a clearly labeled test with one fixed official destination and no caller-supplied URI.</summary>
    public static string CreateTest()
    {
        var registrationUrl = OfficialDestinations.Registration.AbsoluteUri;
        return new XElement("toast",
            new XAttribute("activationType", "protocol"),
            new XAttribute("launch", registrationUrl),
            new XElement("visual",
                new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("text", "SNU Watcher - notification test"),
                    new XElement("text", "This is a test, not a seat opening. Click to open official SNU registration."))),
            new XElement("actions",
                new XElement("action",
                    new XAttribute("content", "Open registration"),
                    new XAttribute("activationType", "protocol"),
                    new XAttribute("arguments", registrationUrl))))
            .ToString(SaveOptions.DisableFormatting);
    }
}
