using SnuWatcher.Application.Delivery.Batches;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

public sealed class BatchChannelContractTests
{
    [Fact]
    public void DefaultReadinessCannotAuthorizeNativeSubmission() =>
        Assert.Equal(BatchChannelReadiness.Initializing, default);

    [Theory]
    [InlineData(BatchChannelOutcome.OsAccepted)]
    [InlineData(BatchChannelOutcome.RetryableFailure)]
    [InlineData(BatchChannelOutcome.AcceptanceUnknown)]
    public void OnlyProvenNoSubmissionCarriesANoSubmissionReason(BatchChannelOutcome outcome)
    {
        Assert.Equal(outcome, new BatchChannelResult(outcome).Outcome);
        Assert.Throws<ArgumentException>(() => new BatchChannelResult(outcome, BatchNoSubmissionReason.Cancelled));
        Assert.Throws<ArgumentException>(() => new BatchChannelResult(BatchChannelOutcome.NotSubmitted));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchChannelResult((BatchChannelOutcome)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchChannelResult(BatchChannelOutcome.NotSubmitted, (BatchNoSubmissionReason)99));
    }

    [Fact]
    public void ProvenNoSubmissionKeepsTypedEvidenceAndNoArbitraryDiagnostic()
    {
        var result = new BatchChannelResult(BatchChannelOutcome.NotSubmitted, BatchNoSubmissionReason.ChannelNotReady);
        Assert.Equal(BatchChannelOutcome.NotSubmitted, result.Outcome);
        Assert.Equal(BatchNoSubmissionReason.ChannelNotReady, result.NoSubmissionReason);
    }

    [Fact]
    public void SubmissionRetainsSealedFactsAndNormalizesOnlyUtcOffsets()
    {
        var envelope = BatchFixture.Envelope();
        var expires = BatchFixture.Now.AddSeconds(30);
        var submission = new BatchDesktopSubmission(envelope, BatchFixture.Now.ToOffset(TimeSpan.FromHours(9)),
            expires.ToOffset(TimeSpan.FromHours(9)), BatchFixture.Policy().MaxAge);
        Assert.Same(envelope, submission.Envelope);
        Assert.Equal(BatchFixture.Now, submission.ValidatedAtUtc);
        Assert.Equal(TimeSpan.Zero, submission.ValidatedAtUtc.Offset);
        Assert.Equal(expires, submission.EligibilityExpiresAtUtc);
        Assert.Equal(BatchFixture.Policy().MaxAge, submission.MaxSourceAge);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("expiry")]
    [InlineData("age_zero")]
    [InlineData("age_large")]
    public void InvalidSubmissionWindowCannotReachNativeBoundary(string mode)
    {
        var validated = mode == "future" ? BatchFixture.Now.AddMinutes(-1)
            : mode == "stale" ? BatchFixture.Now.AddMinutes(3) : BatchFixture.Now;
        var age = mode == "age_zero" ? TimeSpan.Zero
            : mode == "age_large" ? TimeSpan.FromDays(1) + TimeSpan.FromTicks(1) : BatchFixture.Policy().MaxAge;
        Assert.ThrowsAny<ArgumentException>(() => new BatchDesktopSubmission(BatchFixture.Envelope(), validated,
            mode == "expiry" ? BatchFixture.Now : null, age));
    }

    [Fact]
    public void SourceAgeBoundaryIsInclusiveButRecipientExpiryIsExclusive()
    {
        var envelope = BatchFixture.Envelope();
        var at = envelope.CurrentSuccessAtUtc + BatchFixture.Policy().MaxAge;
        Assert.Equal(at, new BatchDesktopSubmission(envelope, at, at.AddTicks(1), BatchFixture.Policy().MaxAge).ValidatedAtUtc);
        Assert.Throws<ArgumentException>(() => new BatchDesktopSubmission(envelope, at, at, BatchFixture.Policy().MaxAge));
        Assert.Throws<ArgumentNullException>(() => new BatchDesktopSubmission(null!, at, null, BatchFixture.Policy().MaxAge));
    }
}
