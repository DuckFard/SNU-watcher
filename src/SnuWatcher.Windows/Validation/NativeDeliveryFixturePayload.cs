using System.Text;
using System.Xml;
using System.Xml.Linq;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Notifications;

namespace SnuWatcher.Windows.Validation;

/// <summary>Qualification-only labelling of a real pipeline opening; no outcome, identity or destination is fabricated.</summary>
public static class NativeDeliveryFixturePayload
{
    /// <summary>Prefixes TEST and synthetic-not-live wording while preserving counts, dedupe, audio, expiration and HTTPS activation.</summary>
    public static NativeToastPayload LabelOpening(NativeToastPayload opening, UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(opening);
        if (!Enum.IsDefined(language) || opening.ExpiresAtUtc is null || Encoding.UTF8.GetByteCount(opening.Xml) > NativeNotificationPayload.MaximumUtf8Bytes)
            throw Invalid();
        if (opening.Tag.Length != 16 || opening.Group.Length != 16 || !Guid.TryParseExact(opening.Tag + opening.Group, "N", out var identity)
            || identity == Guid.Empty || identity.ToString("N") != opening.Tag + opening.Group) throw Invalid();
        try
        {
            using var text = new StringReader(opening.Xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = NativeNotificationPayload.MaximumUtf8Bytes });
            var document = XDocument.Load(reader);
            var root = document.Root ?? throw Invalid();
            var destination = OfficialDestinations.Registration.AbsoluteUri;
            Require(root.Name == "toast" && Attributes(root, "activationType", "launch")
                && (string?)root.Attribute("activationType") == "protocol" && (string?)root.Attribute("launch") == destination);
            Require(root.Elements().Select(element => element.Name.LocalName).SequenceEqual(
                root.Element("audio") is null ? ["visual", "actions"] : ["visual", "actions", "audio"]));
            var visual = root.Element("visual") ?? throw Invalid();
            var binding = visual.Element("binding") ?? throw Invalid();
            Require(!visual.HasAttributes && visual.Elements().Count() == 1 && binding.Name == "binding"
                && Attributes(binding, "template") && (string?)binding.Attribute("template") == "ToastGeneric");
            var messages = binding.Elements().ToArray();
            Require(messages.Length == 2 && messages.All(element => element.Name == "text" && !element.HasAttributes && !element.HasElements));
            var actions = root.Element("actions") ?? throw Invalid();
            var action = actions.Element("action") ?? throw Invalid();
            Require(!actions.HasAttributes && actions.Elements().Count() == 1 && Attributes(action, "content", "activationType", "arguments")
                && (string?)action.Attribute("activationType") == "protocol" && (string?)action.Attribute("arguments") == destination && !action.HasElements);
            if (root.Element("audio") is { } audio)
                Require(Attributes(audio, "silent") && (string?)audio.Attribute("silent") == "true" && !audio.HasElements);
            messages[0].Value = (language == UiLanguage.English ? "TEST — " : "테스트 — ") + messages[0].Value;
            messages[1].Value = (language == UiLanguage.English
                ? "Synthetic fixture counts; not live SNU availability.\n" : "가상 표본 수치이며 실제 서울대 실시간 여석이 아닙니다.\n") + messages[1].Value;
            var xml = document.ToString(SaveOptions.DisableFormatting);
            Require(Encoding.UTF8.GetByteCount(xml) <= NativeNotificationPayload.MaximumUtf8Bytes);
            return new(xml, opening.Tag, opening.Group, opening.ExpiresAtUtc);
        }
        catch (XmlException) { throw Invalid(); }
    }

    private static bool Attributes(XElement element, params string[] expected) => element.Attributes().Count() == expected.Length
        && element.Attributes().All(attribute => !attribute.IsNamespaceDeclaration && attribute.Name.NamespaceName.Length == 0
            && expected.Contains(attribute.Name.LocalName, StringComparer.Ordinal));
    private static void Require(bool condition) { if (!condition) throw Invalid(); }
    private static ArgumentException Invalid() => new("The fixture notification payload is not a bounded canonical opening.");
}
