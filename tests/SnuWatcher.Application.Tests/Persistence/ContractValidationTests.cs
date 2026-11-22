using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Persistence;

public sealed class ContractValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
    private static SectionKey Key(string section = "012") => new(2026, "2", "U000200002", "F32.102", section, "000");
    private static CheckAttemptToken Token(long ordinal = 1) => new(Guid.NewGuid(), ordinal);

    [Fact]
    public void CheckTokenRejectsEmptyIdentityAndNonpositiveOrder()
    {
        Assert.Throws<ArgumentException>(() => new CheckAttemptToken(Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Token(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Token(-1));
    }

    [Fact]
    public void IssuedScopeIsAUniqueImmutableSnapshot()
    {
        var input = new List<SectionKey> { Key(), Key(), Key("013") };
        var issued = new IssuedCheck(Token(), Now, input);
        input.Clear();
        Assert.Equal(2, issued.Scope.Length);
        Assert.Contains(Key(), issued.Scope);
        Assert.Throws<ArgumentException>(() => new CheckIssue(Now, []));
        Assert.Throws<ArgumentException>(() => new CheckIssue(Now, Enumerable.Repeat(Key(), PersistenceLimits.MaxSections + 1)));
    }

    [Fact]
    public void CompletionValidatesTokenOrderOutcomeAndDiagnostic()
    {
        var token = Token(2);
        var valid = SeatObservation.Valid(Key(), 2, Now, new SeatCount(119, 120), QuotaMeaning.EnrolledOverQuota);
        var completion = new SectionCompletion(token, valid, CheckOutcome.Success, null);
        Assert.Equal(Key(), completion.Key);
        Assert.Equal(Now, completion.ObservedAtUtc);
        Assert.Throws<ArgumentException>(() => new SectionCompletion(Token(3), valid, CheckOutcome.Success, null));
        Assert.Throws<ArgumentException>(() => new SectionCompletion(token, valid, CheckOutcome.Unavailable, null));
        Assert.Throws<ArgumentException>(() => new SectionCompletion(token, SeatObservation.Unknown(Key(), 2, Now), CheckOutcome.Success, null));
        Assert.Throws<ArgumentException>(() => new SectionCompletion(token, valid, CheckOutcome.Success, "raw secret or URL"));
    }

    [Fact]
    public void NewSubscriptionSnapshotsMembershipAndAllowsExplicitSameTermMulticourseGroup()
    {
        var input = new List<SectionKey> { Key(), new(2026, "2", "U000200002", "F33.107", "008", "000") };
        var command = new NewSubscription(Guid.NewGuid(), "  Any class  ", SubscriptionKind.Group, input, true, null, Now);
        input.Clear();
        Assert.Equal("Any class", command.Label);
        Assert.Equal(2, command.Sections.Length);
        Assert.Throws<ArgumentException>(() => new NewSubscription(Guid.NewGuid(), "x", SubscriptionKind.Section, command.Sections, true, null, Now));
        Assert.Throws<ArgumentException>(() => new NewSubscription(Guid.NewGuid(), "x", SubscriptionKind.Group,
            [Key(), new(2025, "2", "U000200002", "F32.102", "012", "000")], true, null, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a\nb")]
    public void LabelsRejectEmptyWhitespaceAndControlCharacters(string label)
    {
        Assert.Throws<ArgumentException>(() => new NewSubscription(Guid.NewGuid(), label, SubscriptionKind.Section, [Key()], true, null, Now));
    }

    [Fact]
    public void LifecycleCommandsValidateExpectedRevisionAndKeepTypedPayloads()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PauseSubscription(id, 0, Now));
        Assert.Throws<ArgumentException>(() => new RemoveSubscription(Guid.Empty, 1, Now));
        var rename = new RenameSubscription(id, 3, Now, " renamed ");
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 12, 1));
        Assert.Equal("renamed", rename.Label);
        Assert.Equal(3, rename.ExpectedRevision);
        Assert.Equal(expiry, new SetSubscriptionExpiry(id, 3, Now, expiry).Expiry);
        Assert.Equal(id, new ResumeSubscription(id, 3, Now).Id);
    }

    [Fact]
    public void SectionSnapshotKeepsNewerUnknownHealthSeparateFromOlderValidBaseline()
    {
        var baseline = new SeatBaseline(Key(), new SeatCount(119, 120), QuotaMeaning.EnrolledOverQuota, 1, Now);
        var snapshot = new SectionSnapshot(Key(), 2, 3, CheckOutcome.Unavailable, Now.AddMinutes(1), Now, baseline);
        Assert.Equal(3, snapshot.LatestCompletedOrdinal);
        Assert.Equal(1, snapshot.Baseline!.AttemptSequence);
        Assert.Equal(Now, snapshot.LastSuccessfulAtUtc);
        Assert.Throws<ArgumentException>(() => new SectionSnapshot(Key("013"), 2, 3, CheckOutcome.Unavailable, Now, Now, baseline));
        Assert.Throws<ArgumentException>(() => new SectionSnapshot(Key(), 1, 0, CheckOutcome.Success, Now, Now, baseline));
    }

    [Fact]
    public void MemberInitializationIsBoundToGenerationAndActivationWatermark()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemberSnapshot(Key(), -1, null));
        Assert.Throws<ArgumentException>(() => new MemberSnapshot(Key(), 4, 4));
        var member = new MemberSnapshot(Key(), 4, 5);
        var effect = new MemberInitializationEffect(Guid.NewGuid(), 2);
        Assert.Equal(5, member.InitializedByAttemptOrdinal);
        Assert.Equal(2, effect.Generation);
    }

    [Fact]
    public void CommitResultsRequireReceiptExactlyForCommittedOrReplayedOutcomes()
    {
        var token = Token();
        var receipt = new SectionCommitReceipt(token, Key(), CommitDisposition.AcceptedValid, 1, Guid.NewGuid());
        Assert.Equal(receipt, TryCommitResult.Committed(receipt).Receipt);
        Assert.Equal(CommitWriteStatus.Replayed, TryCommitResult.Replayed(receipt).Status);
        Assert.Null(TryCommitResult.VersionConflict().Receipt);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SectionCommitReceipt(token, Key(), CommitDisposition.AcceptedValid, -1, null));
    }

    [Fact]
    public void StaleEffectsCanPreserveSuccessfulHealthWithoutWritingABaseline()
    {
        var effects = new SectionEffects(EvaluationOutcome.IgnoredStale, CheckOutcome.Success, null, [], null);
        Assert.Equal(CheckOutcome.Success, effects.HealthOutcome);
        Assert.Null(effects.NextBaseline);
    }

    [Fact]
    public void SuccessfulSnapshotRequiresTheSameCompletedValidObservation()
    {
        Assert.Throws<ArgumentException>(() => new SectionSnapshot(Key(), 1, 1, CheckOutcome.Success, Now, null, null));
        var old = new SeatBaseline(Key(), new SeatCount(120, 120), QuotaMeaning.EnrolledOverQuota, 1, Now);
        Assert.Throws<ArgumentException>(() => new SectionSnapshot(Key(), 2, 2, CheckOutcome.Success, Now.AddMinutes(1), Now, old));
    }
}
