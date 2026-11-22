using System.Text;
using System.Xml.Linq;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Notifications;
using Xunit;

namespace SnuWatcher.Windows.Tests.Notifications;

public sealed class NativePayloadTests
{
    [Theory]
    [InlineData(UiLanguage.English, "Open registration", "does not reserve")]
    [InlineData(UiLanguage.Korean, "수강신청 열기", "좌석을 예약하지")]
    public void OpeningHasExactIdentityOriginalTimeAndOnlyFixedHttpsActivation(UiLanguage language, string button, string warning)
    {
        var submission = NativeNotificationFixture.Submission();
        var payload = NativeNotificationPayload.CreateOpening(submission, language, false);
        var xml = XElement.Parse(payload.Xml);
        Assert.Equal("protocol", xml.Attribute("activationType")?.Value);
        Assert.Equal(OfficialDestinations.Registration.AbsoluteUri, xml.Attribute("launch")?.Value);
        var action = Assert.Single(xml.Descendants("action"));
        Assert.Equal(button, action.Attribute("content")?.Value);
        Assert.Equal(OfficialDestinations.Registration.AbsoluteUri, action.Attribute("arguments")?.Value);
        Assert.Equal("protocol", action.Attribute("activationType")?.Value);
        Assert.Contains("F32.102", payload.Xml);
        Assert.Contains("012", payload.Xml);
        Assert.Contains("116/120", payload.Xml);
        Assert.Contains("12:00:00", payload.Xml);
        Assert.Contains(warning, payload.Xml);
        Assert.Empty(xml.Descendants("audio"));
        Assert.True(Encoding.UTF8.GetByteCount(payload.Xml) <= NativeNotificationPayload.MaximumUtf8Bytes);
        Assert.Equal(submission.Envelope.NativeDedupeKey, payload.Tag + payload.Group);
        Assert.Equal(16, payload.Tag.Length); Assert.Equal(16, payload.Group.Length);
        Assert.Equal(NativeNotificationFixture.Now.AddMinutes(2), payload.ExpiresAtUtc);
    }

    [Fact]
    public void SameBatchDifferentAttemptReusesFullPairButOtherBatchCannotCollide()
    {
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var first = NativeNotificationPayload.CreateOpening(NativeNotificationFixture.Submission(batchId: id), UiLanguage.English, true);
        var retry = NativeNotificationPayload.CreateOpening(NativeNotificationFixture.Submission(batchId: id), UiLanguage.English, true);
        var other = NativeNotificationPayload.CreateOpening(NativeNotificationFixture.Submission(), UiLanguage.English, true);
        Assert.Equal((first.Tag, first.Group), (retry.Tag, retry.Group));
        Assert.NotEqual((first.Tag, first.Group), (other.Tag, other.Group));
        Assert.Equal("true", XElement.Parse(first.Xml).Element("audio")?.Attribute("silent")?.Value);
    }

    [Fact]
    public void TestsAreExplicitlyTestsAndNeverClaimASeatOpened()
    {
        var en = NativeNotificationPayload.CreateTest(Guid.NewGuid(), UiLanguage.English, false);
        var ko = NativeNotificationPayload.CreateTest(Guid.NewGuid(), UiLanguage.Korean, true);
        Assert.Contains("notification test", en.Xml); Assert.Contains("not a seat opening", en.Xml);
        Assert.Contains("알림 테스트", ko.Xml); Assert.Contains("true", ko.Xml);
        Assert.Throws<ArgumentException>(() => NativeNotificationPayload.CreateTest(Guid.Empty, UiLanguage.English, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeNotificationPayload.CreateTest(Guid.NewGuid(), (UiLanguage)99, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xd800)]
    [InlineData(0xdfff)]
    public void MalformedUnicodeAndXmlControlsAreRejectedBeforeDriver(int invalidCodeUnit)
    {
        var invalid = ((char)invalidCodeUnit).ToString();
        Assert.Throws<ArgumentException>(() => NativeNotificationPayload.Build(Guid.NewGuid(), invalid, "body", "action", false));
    }

    [Fact]
    public void HostileTextIsEscapedAndByteBudgetRejectsRatherThanTruncates()
    {
        var payload = NativeNotificationPayload.Build(Guid.NewGuid(), "<toast>\"&😀", "한글\tline\nbody", "<open>", false);
        Assert.Equal("<toast>\"&😀", XElement.Parse(payload.Xml).Descendants("text").First().Value);
        Assert.Single(XElement.Parse(payload.Xml).Descendants("action"));
        Assert.Throws<ArgumentException>(() => NativeNotificationPayload.Build(Guid.NewGuid(), new string('한', 2000), "body", "action", false));
        Assert.Throws<ArgumentException>(() => NativeNotificationPayload.Build(Guid.NewGuid(), new string('x', 4097), "body", "action", false));
        Assert.Throws<ArgumentNullException>(() => NativeNotificationPayload.Build(Guid.NewGuid(), null!, "body", "action", false));
        Assert.Throws<ArgumentNullException>(() => NativeNotificationPayload.CreateOpening(null!, UiLanguage.English, false));
        Assert.Equal(NativeNotificationFixture.Now.AddSeconds(30), NativeNotificationPayload.CreateOpening(
            NativeNotificationFixture.Submission(NativeNotificationFixture.Now.AddSeconds(30)), UiLanguage.English, false).ExpiresAtUtc);
    }
}
