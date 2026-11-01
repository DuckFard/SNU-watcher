using SnuWatcher.Application.Monitoring.SourcePolicy;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.SourcePolicy;

public sealed class SourcePolicyContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IdentityAndEnumBoundariesRejectUnsupportedSources()
    {
        Assert.Throws<ArgumentException>(() => new SourceRequestIssue("https://elsewhere.invalid", SourceRequestKind.SearchPage));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceRequestIssue(SourcePolicyIds.SnuSugang, (SourceRequestKind)999));
        Assert.Throws<ArgumentException>(() => new SourceRequestToken(Guid.Empty, SourcePolicyIds.SnuSugang, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceRequestToken(Guid.NewGuid(), SourcePolicyIds.SnuSugang, 0));
    }

    [Fact]
    public void RetryAfterKeepsTypedDateEvidenceAndNormalizesNegativeDelta()
    {
        Assert.Equal(TimeSpan.Zero, SourceRetryAfter.Delta(TimeSpan.FromSeconds(-1)).DelayAt(Now));
        var evidence = SourceRetryAfter.HttpDate(Now.AddHours(6).ToOffset(TimeSpan.FromHours(9)));
        Assert.Equal(SourceRetryAfterKind.HttpDate, evidence.Kind);
        Assert.Equal(TimeSpan.Zero, evidence.HttpDateUtc!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(6), evidence.DelayAt(Now));
        Assert.Equal(TimeSpan.Zero, evidence.DelayAt(Now.AddDays(1)));
        var resolved = SourceRetryAfter.ResolvedDelay(TimeSpan.FromMinutes(90));
        Assert.Equal(SourceRetryAfterKind.ResolvedDelay, resolved.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), resolved.DelayAt(Now));
        Assert.Null(resolved.HttpDateUtc);
    }

    [Fact]
    public void NonHttpResultPreservesUnknownStartAndReceiptAndClockChangedTimes()
    {
        var token = new SourceRequestToken(Guid.NewGuid(), SourcePolicyIds.SnuSugang, 1);
        var network = new SourceRequestResultCommand(token, SourceRequestOutcome.NetworkFailure, Now,
            diagnosticCode: "network_unavailable");
        Assert.Null(network.ActualStartedAtUtc);
        Assert.Null(network.ReceivedAtUtc);
        var clockChanged = new SourceRequestResultCommand(token, SourceRequestOutcome.Success, Now,
            Now.AddMinutes(1), Now.AddMinutes(-1));
        Assert.Equal(Now.AddMinutes(1), clockChanged.ActualStartedAtUtc);
        Assert.Equal(Now.AddMinutes(-1), clockChanged.ReceivedAtUtc);
        Assert.Throws<ArgumentException>(() => new SourceRequestResultCommand(token, SourceRequestOutcome.RateLimited,
            Now, retryAfter: SourceRetryAfter.Delta(TimeSpan.FromSeconds(3))));
    }

    [Fact]
    public void DiagnosticsCannotContainUnboundedOrControlText()
    {
        var token = new SourceRequestToken(Guid.NewGuid(), SourcePolicyIds.SnuSugang, 1);
        Assert.Throws<ArgumentException>(() => new SourceRequestResultCommand(token, SourceRequestOutcome.Unavailable,
            Now, diagnosticCode: "bad\nmessage"));
        Assert.Throws<ArgumentException>(() => new SourceRequestResultCommand(token, SourceRequestOutcome.Unavailable,
            Now, diagnosticCode: new string('x', 129)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceAccessResumeCommand(SourcePolicyIds.SnuSugang, -1));
    }

    [Theory]
    [InlineData("C:/Users/student/private.txt")]
    [InlineData("secret message")]
    [InlineData("HTTP_ERROR")]
    [InlineData("코드")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void DiagnosticsAreMachineCodesRatherThanPrintablePrivateText(string code)
    {
        var token = new SourceRequestToken(Guid.NewGuid(), SourcePolicyIds.SnuSugang, 1);
        Assert.Throws<ArgumentException>(() => new SourceRequestResultCommand(token, SourceRequestOutcome.Unavailable,
            Now, diagnosticCode: code));
    }

    [Fact]
    public void SnapshotHasCoherentProgressAndExclusiveCooldownBoundary()
    {
        var initial = SourcePolicySnapshot.Initial(SourcePolicyIds.SnuSugang);
        Assert.Equal(0, initial.Revision);
        Assert.False(initial.IsBlockedAt(Now));
        var cooldown = new SourceCooldown(Now, TimeSpan.FromMinutes(1));
        var active = new SourcePolicySnapshot(SourcePolicyIds.SnuSugang, 1, SourceRequestProgress.Empty,
            cooldown: cooldown);
        Assert.True(active.IsBlockedAt(Now.AddSeconds(59)));
        Assert.False(active.IsBlockedAt(Now.AddMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceRequestProgress(1, Now, 2,
            SourceRequestOutcome.Success, Now, 0, null));
    }

    [Fact]
    public void LatestSuccessMustAlsoBeTheLastSuccessfulOrdinal()
    {
        Assert.Throws<ArgumentException>(() => new SourceRequestProgress(2, Now, 2,
            SourceRequestOutcome.Success, Now, 1, Now));
    }

    [Fact]
    public void LatestFailureCannotClaimItsOwnOrdinalAsSuccessful()
    {
        Assert.Throws<ArgumentException>(() => new SourceRequestProgress(2, Now, 2,
            SourceRequestOutcome.NetworkFailure, Now, 2, Now));
    }

    [Fact]
    public void SameSuccessfulOrdinalMustHaveOneTruthfulCompletionTimestamp()
    {
        Assert.Throws<ArgumentException>(() => new SourceRequestProgress(2, Now, 2,
            SourceRequestOutcome.Success, Now, 2, Now.AddSeconds(1)));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    public void AccessPauseMustComeFromACompletedAttemptAfterTheResumeBarrier(long causingOrdinal, long resumeWatermark)
    {
        var progress = new SourceRequestProgress(2, Now, 1, SourceRequestOutcome.AccessRestricted, Now, 0, null);
        Assert.Throws<ArgumentException>(() => new SourcePolicySnapshot(SourcePolicyIds.SnuSugang, 2, progress,
            new SourceAccessPause(causingOrdinal, Now, "access_restricted"), resumeWatermark: resumeWatermark));
    }

    [Fact]
    public void PauseOnTheLatestRequestRequiresAnAccessRestrictedOutcome()
    {
        var progress = new SourceRequestProgress(1, Now, 1, SourceRequestOutcome.Success, Now, 1, Now);
        Assert.Throws<ArgumentException>(() => new SourcePolicySnapshot(SourcePolicyIds.SnuSugang, 2, progress,
            new SourceAccessPause(1, Now, "access_restricted")));
    }
}
