using System.Xml.Linq;
using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class RegistrationNotificationPayloadTests
{
    private const string RegistrationUrl = "https://sugang.snu.ac.kr/sugang/co/co010.action";

    [Fact]
    public void BodyOpensOnlyTheFixedOfficialHttpsPageThroughWindows()
    {
        var toast = XElement.Parse(RegistrationNotificationPayload.CreateTest());

        Assert.Equal("protocol", (string?)toast.Attribute("activationType"));
        Assert.Equal(RegistrationUrl, (string?)toast.Attribute("launch"));
        Assert.Equal(Uri.UriSchemeHttps, new Uri((string)toast.Attribute("launch")!).Scheme);
    }

    [Fact]
    public void SingleClearButtonOpensTheSameFixedPageWithoutAnAppCallback()
    {
        var toast = XElement.Parse(RegistrationNotificationPayload.CreateTest());
        var action = Assert.Single(toast.Element("actions")!.Elements("action"));

        Assert.Equal("Open registration", (string?)action.Attribute("content"));
        Assert.Equal("protocol", (string?)action.Attribute("activationType"));
        Assert.Equal(RegistrationUrl, (string?)action.Attribute("arguments"));
        Assert.Equal((string?)toast.Attribute("launch"), (string?)action.Attribute("arguments"));
    }

    [Fact]
    public void TestPopupCannotBeMistakenForASeatOpening()
    {
        var toast = XElement.Parse(RegistrationNotificationPayload.CreateTest());
        var text = toast.Element("visual")!.Element("binding")!.Elements("text").Select(element => element.Value).ToArray();

        Assert.Equal("SNU Watcher - notification test", text[0]);
        Assert.Contains("This is a test, not a seat opening.", text[1], StringComparison.Ordinal);
        Assert.Contains("official SNU registration", text[1], StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadIsStandaloneToastGenericXmlWithNoAdditionalActions()
    {
        var document = XDocument.Parse(RegistrationNotificationPayload.CreateTest());
        var toast = document.Root!;

        Assert.Equal("toast", toast.Name.LocalName);
        Assert.Equal("ToastGeneric", (string?)toast.Element("visual")!.Element("binding")!.Attribute("template"));
        Assert.Equal(2, toast.Elements().Count());
        Assert.Single(toast.Descendants("action"));
        Assert.DoesNotContain("view-section", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("action=open-registration", document.ToString(), StringComparison.Ordinal);
    }
}
