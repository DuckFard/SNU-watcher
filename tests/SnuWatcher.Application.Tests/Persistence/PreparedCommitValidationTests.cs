using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Persistence;

public sealed class PreparedCommitValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly SectionKey Key = new(2026, "2", "U000200002", "F32.102", "012", "000");
    private static readonly CheckAttemptToken Token = new(Guid.NewGuid(), 1);
    private static readonly SeatBaseline Baseline = new(Key, new(119, 120), QuotaMeaning.EnrolledOverQuota, 1, Now);
    private static SectionCompletion ValidCompletion() => new(Token,
        SeatObservation.Valid(Key, 1, Now, Baseline.Seats, Baseline.Meaning), CheckOutcome.Success);

    [Fact]
    public void PreparedBaselineCannotChangeSourceCountsOrMeaning()
    {
        var completion = ValidCompletion();
        var wrongCounts = Baseline with { Seats = new(118, 120) };
        var wrongMeaning = Baseline with { Meaning = new("different") };
        foreach (var wrong in new[] { wrongCounts, wrongMeaning })
        {
            var effects = new SectionEffects(EvaluationOutcome.NoOpening, CheckOutcome.Success, wrong, [], null);
            Assert.Throws<ArgumentException>(() => new PreparedSectionCommit(completion, 0, 1, effects, Now));
        }
    }

    [Fact]
    public void EffectsMustMatchObservationKindAndKnownFailureHealth()
    {
        var unknownEffects = new SectionEffects(EvaluationOutcome.IgnoredUnknown, CheckOutcome.Unavailable, null, [], null);
        Assert.Throws<ArgumentException>(() => new PreparedSectionCommit(ValidCompletion(), 0, 1, unknownEffects, Now));
        var unknown = new SectionCompletion(Token, SeatObservation.Unknown(Key, 1, Now), CheckOutcome.Unavailable);
        var acceptedEffects = new SectionEffects(EvaluationOutcome.NoOpening, CheckOutcome.Success, Baseline, [], null);
        Assert.Throws<ArgumentException>(() => new PreparedSectionCommit(unknown, 0, 1, acceptedEffects, Now));
        var wrongHealth = new SectionEffects(EvaluationOutcome.IgnoredUnknown, CheckOutcome.RateLimited, null, [], null);
        Assert.Throws<ArgumentException>(() => new PreparedSectionCommit(unknown, 0, 1, wrongHealth, Now));
    }

    [Theory]
    [InlineData("not-a-subscription", 1, 0)]
    [InlineData("00000000000000000000000000000000", 1, 0)]
    [InlineData("11111111111111111111111111111111", 0, 0)]
    [InlineData("11111111111111111111111111111111", 1, 99)]
    public void EffectsRejectMalformedRecipientIdentityGenerationAndReason(string id, long generation, int reason)
    {
        var trigger = new OpeningNotification(Key, 1, 0, 1, Now, [new(id, generation, (OpeningReason)reason)]);
        Assert.Throws<ArgumentException>(() => new SectionEffects(EvaluationOutcome.Opening, CheckOutcome.Success, Baseline, [], trigger));
    }

    [Fact]
    public void EffectsRejectDuplicateRecipientAssociations()
    {
        var recipient = new Recipient(Guid.NewGuid().ToString("N"), 1, OpeningReason.IncreasedSeats);
        var trigger = new OpeningNotification(Key, 1, 0, 1, Now, [recipient, recipient]);
        Assert.Throws<ArgumentException>(() => new SectionEffects(EvaluationOutcome.Opening, CheckOutcome.Success, Baseline, [], trigger));
    }

    [Fact]
    public void DefaultImmutableArrayIsRejectedAsAnUninitializedScope()
    {
        var empty = default(ImmutableArray<SectionKey>);
        Assert.Throws<ArgumentException>(() => new CheckIssue(Now, empty));
    }
}
