using Xunit;

namespace SnuWatcher.Core.Tests;

public sealed class ImmutableResultTests
{
    private static readonly Recipient Original = new("watch", 1, OpeningReason.InitialAvailability);

    [Fact]
    public void NotificationSnapshotsConstructorAndWithExpressionInputs()
    {
        var input = new List<Recipient> { Original };
        var notification = new OpeningNotification(Fixture.Key(), 1, null, 1, Fixture.At(1), input);
        input.Clear();
        Assert.Equal(Original, Assert.Single(notification.Recipients));

        var replacement = new List<Recipient> { Original with { Generation = 2 } };
        var updated = notification with { Recipients = replacement };
        replacement.Clear();
        Assert.Equal(2, Assert.Single(updated.Recipients).Generation);
        Assert.Equal(1, Assert.Single(notification.Recipients).Generation);
    }

    [Fact]
    public void ResolutionExposesReadOnlyRecipientAndInitializationCollections()
    {
        var key = Fixture.Key();
        var observation = Fixture.Valid(key, 1, 119, 120);
        var evaluation = OpeningPolicy.Evaluate(null, observation);
        var result = RecipientResolver.Resolve([Fixture.Member("watch", key)], observation, evaluation, Fixture.At(1));

        var recipients = Assert.IsAssignableFrom<IList<Recipient>>(result.Notification!.Recipients);
        var initializations = Assert.IsAssignableFrom<IList<MemberInitialization>>(result.Initializations);
        Assert.Throws<NotSupportedException>(() => recipients.Clear());
        Assert.Throws<NotSupportedException>(() => initializations.Clear());
    }

    [Fact]
    public void ResolutionSnapshotsConstructorAndWithExpressionInputs()
    {
        var input = new List<MemberInitialization> { new("watch", 1) };
        var resolution = new RecipientResolution(null, input);
        input.Clear();
        Assert.Equal(1, Assert.Single(resolution.Initializations).Generation);

        var replacement = new List<MemberInitialization> { new("watch", 2) };
        var updated = resolution with { Initializations = replacement };
        replacement.Clear();
        Assert.Equal(2, Assert.Single(updated.Initializations).Generation);
        Assert.Equal(1, Assert.Single(resolution.Initializations).Generation);
    }
}
