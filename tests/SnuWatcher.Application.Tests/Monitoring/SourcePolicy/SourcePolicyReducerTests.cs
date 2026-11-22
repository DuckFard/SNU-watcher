using SnuWatcher.Application.Monitoring.SourcePolicy;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.SourcePolicy;

public sealed class SourcePolicyReducerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Source = SourcePolicyIds.SnuSugang;

    [Theory]
    [InlineData(SourceRequestOutcome.RateLimited, 60)]
    [InlineData(SourceRequestOutcome.Maintenance, 120)]
    public void DefaultGlobalDelaysAndLongServerEvidenceAreHonored(SourceRequestOutcome outcome, int seconds)
    {
        var before = Issued(1);
        var fallback = SourcePolicyReducer.Apply(before, Request(1), Result(1, outcome));
        Assert.True(fallback.Applied);
        Assert.Equal(Now.AddSeconds(seconds), fallback.Policy.Cooldown!.NotBeforeUtc);
        var longer = SourcePolicyReducer.Apply(before, Request(1), Result(1, outcome, TimeSpan.FromHours(3)));
        Assert.Equal(Now.AddHours(3), longer.Policy.Cooldown!.NotBeforeUtc);
    }

    [Fact]
    public void Only503WithRetryAfterCreatesOtherUnavailableGlobalCooldown()
    {
        var before = Issued(1);
        Assert.Null(SourcePolicyReducer.Apply(before, Request(1), Result(1, SourceRequestOutcome.Unavailable,
            TimeSpan.FromHours(1))).Policy.Cooldown);
        Assert.Null(SourcePolicyReducer.Apply(before, Request(1), Result(1, SourceRequestOutcome.ServiceUnavailable)).Policy.Cooldown);
        Assert.Equal(Now.AddHours(1), SourcePolicyReducer.Apply(before, Request(1),
            Result(1, SourceRequestOutcome.ServiceUnavailable, TimeSpan.FromHours(1))).Policy.Cooldown!.NotBeforeUtc);
    }

    [Fact]
    public void SuccessAndOlderResultsCannotClearPauseOrShortenCooldown()
    {
        var blocked = new SourcePolicySnapshot(Source, 4, new SourceRequestProgress(3, Now, 1,
            SourceRequestOutcome.AccessRestricted, Now, 0, null),
            new SourceAccessPause(1, Now, "access_restricted"), new SourceCooldown(Now, TimeSpan.FromHours(2)));
        var success = SourcePolicyReducer.Apply(blocked, Request(3), Result(3, SourceRequestOutcome.Success));
        Assert.NotNull(success.Policy.AccessPause);
        Assert.Equal(blocked.Cooldown, success.Policy.Cooldown);
        Assert.Equal(3, success.Policy.Progress.LastSuccessfulOrdinal);
        var late = SourcePolicyReducer.Apply(success.Policy, Request(2), Result(2, SourceRequestOutcome.RateLimited,
            TimeSpan.FromDays(1)));
        Assert.False(late.Applied);
        Assert.Same(success.Policy, late.Policy);
    }

    [Fact]
    public void ExplicitResumeRetainsCooldownAndIgnoresOldDeniedAttemptForAccessState()
    {
        var blocked = new SourcePolicySnapshot(Source, 7, new SourceRequestProgress(3, Now, 1,
            SourceRequestOutcome.AccessRestricted, Now, 0, null),
            new SourceAccessPause(1, Now, "access_restricted"), new SourceCooldown(Now, TimeSpan.FromHours(2)));
        var resumed = SourcePolicyReducer.Resume(blocked);
        Assert.Null(resumed.AccessPause);
        Assert.Equal(3, resumed.ResumeWatermark);
        Assert.Equal(blocked.Cooldown, resumed.Cooldown);
        var oldDenied = SourcePolicyReducer.Apply(resumed, Request(3), Result(3, SourceRequestOutcome.AccessRestricted));
        Assert.Null(oldDenied.Policy.AccessPause);
        Assert.Equal(SourceRequestOutcome.AccessRestricted, oldDenied.Policy.Progress.LatestOutcome);
    }

    [Fact]
    public void FailurePreservesSuccessfulEvidenceAndDoesNotTouchSectionFacts()
    {
        var current = new SourcePolicySnapshot(Source, 3, new SourceRequestProgress(2, Now, 1,
            SourceRequestOutcome.Success, Now, 1, Now));
        var failure = SourcePolicyReducer.Apply(current, Request(2), Result(2, SourceRequestOutcome.NetworkFailure));
        Assert.Equal(1, failure.Policy.Progress.LastSuccessfulOrdinal);
        Assert.Equal(Now, failure.Policy.Progress.LastSuccessfulAtUtc);
        Assert.Equal(2, failure.Policy.Progress.LatestCompletedOrdinal);
        Assert.Null(failure.Policy.Cooldown);
    }

    [Fact]
    public void ReceiptBasedDelayDoesNotStartAtLatePersistenceTime()
    {
        var command = new SourceRequestResultCommand(Request(1).Token, SourceRequestOutcome.RateLimited,
            Now.AddMinutes(5), receivedAtUtc: Now, retryAfter: SourceRetryAfter.HttpDate(Now.AddMinutes(10)));
        var applied = SourcePolicyReducer.Apply(Issued(1), Request(1), command);
        Assert.Equal(Now.AddMinutes(10), applied.Policy.Cooldown!.NotBeforeUtc);
    }

    private static SourcePolicySnapshot Issued(long ordinal) => new(Source, ordinal,
        new SourceRequestProgress(ordinal, Now, 0, null, null, 0, null));
    private static IssuedSourceRequest Request(long ordinal) => new(
        new SourceRequestToken(Guid.Parse($"00000000-0000-0000-0000-{ordinal:000000000000}"), Source, ordinal),
        SourceRequestKind.SearchPage, Now, 0);
    private static SourceRequestResultCommand Result(long ordinal, SourceRequestOutcome outcome, TimeSpan? delay = null) =>
        new(Request(ordinal).Token, outcome, Now, receivedAtUtc: Now,
            retryAfter: delay is { } value ? SourceRetryAfter.Delta(value) : null);
}
