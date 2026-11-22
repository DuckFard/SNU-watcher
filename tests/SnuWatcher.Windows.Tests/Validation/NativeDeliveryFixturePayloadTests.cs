using System.Xml.Linq;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Core;
using SnuWatcher.Windows.Notifications;
using SnuWatcher.Windows.Validation;
using Xunit;

namespace SnuWatcher.Windows.Tests.Validation;

public sealed class NativeDeliveryFixturePayloadTests
{
    [Theory]
    [InlineData(UiLanguage.English, false, "TEST")]
    [InlineData(UiLanguage.Korean, true, "테스트")]
    public void FixtureLabelPreservesActualOpeningFactsIdentityAudioExpiryAndFixedProtocol(UiLanguage language, bool muted, string title)
    {
        var original = Opening(language, muted);
        var labelled = NativeDeliveryFixturePayload.LabelOpening(original, language);
        var xml = XDocument.Parse(labelled.Xml);
        var texts = xml.Descendants("text").Select(element => element.Value).ToArray();
        Assert.StartsWith(title, texts[0]);
        Assert.Contains("F32.102 (012)", texts[0]);
        Assert.Contains("39/40", texts[1]);
        Assert.Equal(original.Tag, labelled.Tag); Assert.Equal(original.Group, labelled.Group);
        Assert.Equal(original.ExpiresAtUtc, labelled.ExpiresAtUtc);
        Assert.Equal(XDocument.Parse(original.Xml).Root!.Element("audio")?.ToString(), xml.Root!.Element("audio")?.ToString());
        Assert.Equal(SnuWatcher.Windows.OfficialDestinations.Registration.AbsoluteUri, xml.Root.Attribute("launch")!.Value);
        Assert.Equal(SnuWatcher.Windows.OfficialDestinations.Registration.AbsoluteUri, xml.Descendants("action").Single().Attribute("arguments")!.Value);
        Assert.DoesNotContain("TEST", XDocument.Parse(original.Xml).Descendants("text").First().Value);
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("dtd")]
    [InlineData("extra")]
    [InlineData("large")]
    public void LabellerRejectsNoncanonicalOrOversizedPayloadInsteadOfOpeningAnArbitraryDestination(string mode)
    {
        var original = Opening(UiLanguage.English, false);
        var xml = mode switch
        {
            "destination" => original.Xml.Replace(SnuWatcher.Windows.OfficialDestinations.Registration.AbsoluteUri, "https://attacker.invalid/"),
            "dtd" => "<!DOCTYPE toast [<!ENTITY x SYSTEM 'file:///C:/private'>]>" + original.Xml,
            "extra" => original.Xml.Replace("</toast>", "<unknown/></toast>"),
            _ => original.Xml.Replace("Seat available:", new string('한', 4096)),
        };
        var invalid = new NativeToastPayload(xml, original.Tag, original.Group, original.ExpiresAtUtc);
        Assert.Throws<ArgumentException>(() => NativeDeliveryFixturePayload.LabelOpening(invalid, UiLanguage.English));
    }

    private static NativeToastPayload Opening(UiLanguage language, bool muted)
    {
        var now = DateTimeOffset.UtcNow;
        var key = new SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "000");
        var envelope = new BatchDeliveryEnvelope(Guid.NewGuid(), Guid.NewGuid(), key, new(39, 40), now, 3,
            [new(Guid.NewGuid(), 1, [OpeningReason.IncreasedSeats])], new(1, new string('a', 64), 2, 2));
        return NativeNotificationPayload.CreateOpening(new(envelope, now, null, TimeSpan.FromMinutes(2)), language, muted);
    }
}
