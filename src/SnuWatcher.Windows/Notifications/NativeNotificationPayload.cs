using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Windows.Notifications;

/// <summary>A locally constructed bounded toast with fixed registration activation and complete dedupe identity.</summary>
public sealed record NativeToastPayload
{
    internal NativeToastPayload(string xml, string tag, string group, DateTimeOffset? expiresAtUtc = null)
    { Xml = xml; Tag = tag; Group = group; ExpiresAtUtc = expiresAtUtc?.ToUniversalTime(); }
    /// <summary>Validated XML, never HTML or an arbitrary destination supplied by a caller.</summary>
    public string Xml { get; }
    /// <summary>First half of the complete notification identity, paired with Group.</summary>
    public string Tag { get; }
    /// <summary>Second half of the complete notification identity, paired with Tag.</summary>
    public string Group { get; }
    /// <summary>Opening alerts expire with their original source-age/recipient window; tests keep normal platform lifetime.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }
}

/// <summary>Fixed HTTPS body/button payloads; source counts and times retain their original meaning.</summary>
public static class NativeNotificationPayload
{
    /// <summary>Conservative application UTF-8 cap, below the documented 5 KB XML guidance; not a claimed SDK limit.</summary>
    public const int MaximumUtf8Bytes = 4096;
    /// <summary>Builds an opening alert without inventing a course title, reservation or registration guarantee.</summary>
    public static NativeToastPayload CreateOpening(BatchDesktopSubmission submission, UiLanguage language, bool muted)
    {
        ArgumentNullException.ThrowIfNull(submission); ValidateLanguage(language);
        var envelope = submission.Envelope;
        var identity = envelope.Key.CourseCode + " (" + envelope.Key.Section + ")";
        var counts = envelope.CurrentCounts.Registered.ToString(CultureInfo.InvariantCulture) + "/" + envelope.CurrentCounts.Capacity.ToString(CultureInfo.InvariantCulture);
        var time = envelope.CurrentSuccessAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        var expiry = envelope.CurrentSuccessAtUtc + submission.MaxSourceAge;
        if (submission.EligibilityExpiresAtUtc is { } recipientExpiry && recipientExpiry < expiry) expiry = recipientExpiry;
        return language == UiLanguage.English
            ? Build(envelope.BatchId, "Seat available: " + identity,
                $"{envelope.CurrentSeats.ToString(CultureInfo.InvariantCulture)} available · {counts}\nChecked {time}\nAn alert does not reserve a seat; another student may register first.", "Open registration", muted, expiry)
            : Build(envelope.BatchId, "여석 알림: " + identity,
                $"여석 {envelope.CurrentSeats.ToString(CultureInfo.InvariantCulture)} · {counts}\n확인 {time}\n알림은 좌석을 예약하지 않습니다. 다른 학생이 먼저 신청할 수 있습니다.", "수강신청 열기", muted, expiry);
    }

    /// <summary>Builds an identified test, clearly separate from a real opening.</summary>
    public static NativeToastPayload CreateTest(Guid testId, UiLanguage language, bool muted)
    {
        ValidateLanguage(language);
        return language == UiLanguage.English
            ? Build(testId, "SNU Watcher - notification test", "This is a test, not a seat opening. Click to open official SNU registration.", "Open registration", muted)
            : Build(testId, "SNU Watcher - 알림 테스트", "실제 여석 알림이 아닌 테스트입니다. 클릭하면 서울대학교 수강신청 페이지가 열립니다.", "수강신청 열기", muted);
    }

    internal static NativeToastPayload LegacyTest() => new(RegistrationNotificationPayload.CreateTest(), "native-test", "feasibility");

    internal static NativeToastPayload Build(Guid id, string title, string body, string action, bool muted, DateTimeOffset? expiresAtUtc = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("A notification identity is required.", nameof(id));
        ValidateText(title); ValidateText(body); ValidateText(action);
        var destination = OfficialDestinations.Registration.AbsoluteUri;
        var root = new XElement("toast", new XAttribute("activationType", "protocol"), new XAttribute("launch", destination),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", title), new XElement("text", body))),
            new XElement("actions", new XElement("action", new XAttribute("content", action),
                new XAttribute("activationType", "protocol"), new XAttribute("arguments", destination))));
        if (muted) root.Add(new XElement("audio", new XAttribute("silent", "true")));
        var xml = root.ToString(SaveOptions.DisableFormatting);
        if (Encoding.UTF8.GetByteCount(xml) > MaximumUtf8Bytes)
            throw new ArgumentException("Notification XML exceeds the application byte budget.");
        // The pair contains all 128 bits. Each ASCII half also fits the historical 16-character platform bound.
        var key = id.ToString("N");
        return new NativeToastPayload(xml, key[..16], key[16..], expiresAtUtc);
    }

    private static void ValidateText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumUtf8Bytes) throw new ArgumentException("Notification text exceeds the application budget.");
        try { XmlConvert.VerifyXmlChars(value); }
        catch (XmlException) { throw new ArgumentException("Notification text contains invalid XML characters."); }
    }
    private static void ValidateLanguage(UiLanguage language)
    { if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language)); }
}
