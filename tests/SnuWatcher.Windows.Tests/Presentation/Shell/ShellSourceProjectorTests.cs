using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Windows.Presentation.Shell;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Shell;

public sealed class ShellSourceProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SourceRequestOutcome.Success, "Ui.SourceAvailable")]
    [InlineData(SourceRequestOutcome.ParseChanged, "Ui.SourceChanged")]
    [InlineData(SourceRequestOutcome.RateLimited, "Ui.SourceRateLimited")]
    [InlineData(SourceRequestOutcome.Maintenance, "Ui.SourceMaintenance")]
    [InlineData(SourceRequestOutcome.AccessRestricted, "Ui.SourceRestricted")]
    [InlineData(SourceRequestOutcome.ServiceUnavailable, "Ui.SourceUnavailable")]
    [InlineData(SourceRequestOutcome.Unavailable, "Ui.SourceUnavailable")]
    [InlineData(SourceRequestOutcome.NetworkFailure, "Ui.SourceUnavailable")]
    public void LatestCompletedOutcomeDeterminesSourceMessageWithoutInventingARequest(SourceRequestOutcome outcome, string expected)
    {
        var actual = Project(Stored(outcome), Online());

        Assert.Equal(expected, actual.MessageKey);
        Assert.Equal(Now.AddSeconds(-10), actual.LastCompletedAtUtc);
        Assert.Equal(Now.AddSeconds(-13), actual.ActualStartedAtUtc);
        Assert.Equal(Now.AddSeconds(-11), actual.ReceivedAtUtc);
    }

    [Fact]
    public void MissingStateRemainsUnknownWithUnknownOnlineAndNoInventedFreshnessOrControls()
    {
        var actual = Project();

        Assert.Equal("Ui.SourceUnknown", actual.MessageKey);
        Assert.Null(actual.LastSourceSuccessAtUtc);
        Assert.Null(actual.LastSourceSuccessAge);
        Assert.Null(actual.LastCompletedAtUtc);
        Assert.Null(actual.ActualStartedAtUtc);
        Assert.Null(actual.ReceivedAtUtc);
        Assert.Null(actual.CooldownUntilUtc);
        Assert.Null(actual.NextCheckIn);
        Assert.Null(actual.IsOnline);
        Assert.False(actual.IsSuspended);
        Assert.False(actual.CanResumeAccess);
        Assert.False(actual.CanRecoverPolicy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedStateFailureAndLocalReadFailureTakePriorityOverEveryRemoteAndRuntimeCondition(bool localFailure)
    {
        var failure = Failure(localFailure ? RuntimeFailureKind.Actor : RuntimeFailureKind.SavedState, "runtime_dependency_failed");
        var actual = Project(Stored(SourceRequestOutcome.AccessRestricted, pause: true, cooldown: TimeSpan.FromHours(1)),
            new(false, true, "term_unavailable", TimeSpan.FromSeconds(30)), failure, localSavedStateError: localFailure);

        Assert.Equal("Ui.SavedStateError", actual.MessageKey);
        Assert.Null(actual.NextCheckIn);
        Assert.False(actual.CanResumeAccess);
        Assert.False(actual.CanRecoverPolicy);
        Assert.Equal(Now.AddHours(1), actual.CooldownUntilUtc);
        Assert.Equal(Now.AddSeconds(-10), actual.LastCompletedAtUtc);
    }

    [Fact]
    public void ActorFailureTakesPriorityOverOfflineSuspensionAndRestrictedSource()
    {
        var actual = Project(Stored(SourceRequestOutcome.AccessRestricted, pause: true),
            new(false, true, null, TimeSpan.FromSeconds(30)), Failure(RuntimeFailureKind.Actor, "runtime_query_failed"));

        Assert.Equal("Ui.RuntimeError", actual.MessageKey);
        Assert.Null(actual.NextCheckIn);
        Assert.False(actual.CanResumeAccess);
    }

    [Fact]
    public void OfflineTakesPriorityOverSuspensionAndRemotePolicyButRetainsEvidence()
    {
        var actual = Project(Stored(SourceRequestOutcome.AccessRestricted, pause: true),
            new(false, true, null, TimeSpan.FromSeconds(30)));

        Assert.Equal("Ui.Offline", actual.MessageKey);
        Assert.Equal(false, actual.IsOnline);
        Assert.True(actual.IsSuspended);
        Assert.Null(actual.NextCheckIn);
        Assert.True(actual.CanResumeAccess);
        Assert.Equal(Now.AddMinutes(-2), actual.LastSourceSuccessAtUtc);
    }

    [Fact]
    public void SuspensionTakesPriorityOverRestrictedPolicyAndSuppressesCountdown()
    {
        var actual = Project(Stored(SourceRequestOutcome.AccessRestricted, pause: true),
            new(true, true, null, TimeSpan.FromSeconds(30)));

        Assert.Equal("Ui.Suspended", actual.MessageKey);
        Assert.Null(actual.NextCheckIn);
        Assert.True(actual.IsSuspended);
    }

    [Fact]
    public void AccessPauseTakesPriorityOverCooldownAndMissingTermAndCanBeResumedOnlyWithControls()
    {
        var stored = Stored(SourceRequestOutcome.AccessRestricted, pause: true, cooldown: TimeSpan.FromHours(1));
        var runtime = Online("catalog_term_unavailable");
        var enabled = Project(stored, runtime);
        var disabled = Project(stored, runtime, controlsAvailable: false);

        Assert.Equal("Ui.SourceRestricted", enabled.MessageKey);
        Assert.Null(enabled.NextCheckIn);
        Assert.True(enabled.CanResumeAccess);
        Assert.False(disabled.CanResumeAccess);
    }

    [Fact]
    public void ActiveCooldownTakesPriorityOverMissingTermAndSuccessfulLatestRequest()
    {
        var actual = Project(Stored(SourceRequestOutcome.Success, cooldown: TimeSpan.FromDays(3)),
            Online("term_unavailable"));

        Assert.Equal("Ui.SourceRateLimited", actual.MessageKey);
        Assert.Equal(Now.AddDays(3), actual.CooldownUntilUtc);
        Assert.Equal(TimeSpan.FromSeconds(30), actual.NextCheckIn);
        Assert.False(actual.CanResumeAccess);
    }

    [Theory]
    [InlineData("term_unavailable")]
    [InlineData("catalog_term_unavailable")]
    public void MissingTermTakesPriorityOverLatestSuccessWhenNoActivePolicyGateExists(string reason)
    {
        var actual = Project(Stored(SourceRequestOutcome.Success), Online(reason));

        Assert.Equal("Ui.TermUnavailable", actual.MessageKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CooldownAtOrBeforeDeadlineIsInactiveButItsExactDeadlineIsRetained(int deadlineOffsetSeconds)
    {
        var actual = Project(Stored(SourceRequestOutcome.Success, cooldown: TimeSpan.FromSeconds(deadlineOffsetSeconds)), Online());

        Assert.Equal("Ui.SourceAvailable", actual.MessageKey);
        Assert.Equal(Now.AddSeconds(deadlineOffsetSeconds), actual.CooldownUntilUtc);
    }

    [Fact]
    public void LastSuccessAgeUsesOriginalSuccessfulTimeAndNeverLatestFailureOrReadTime()
    {
        var actual = Project(Stored(SourceRequestOutcome.ParseChanged), Online());

        Assert.Equal(Now.AddMinutes(-2), actual.LastSourceSuccessAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(2), actual.LastSourceSuccessAge);
        Assert.Equal(Now.AddSeconds(-10), actual.LastCompletedAtUtc);
        Assert.Equal("Ui.SourceChanged", actual.MessageKey);
    }

    [Fact]
    public void FutureSuccessAfterWallClockRollbackRetainsTimeWithoutFabricatingAnAge()
    {
        var actual = Project(Stored(SourceRequestOutcome.Success, lastSuccess: Now.AddMinutes(1)), Online());

        Assert.Equal(Now.AddMinutes(1), actual.LastSourceSuccessAtUtc);
        Assert.Null(actual.LastSourceSuccessAge);
        Assert.Equal("Ui.SourceAvailable", actual.MessageKey);
    }

    [Fact]
    public void MissingPhysicalEvidenceDoesNotPromoteIssuedIntentToActualStartOrReceipt()
    {
        var progress = new SourceRequestProgress(1, Now.AddSeconds(-2), 0, null, null, 0, null);
        var stored = new SourceStatusSnapshot(new(0, 0, Now), new(SourcePolicyIds.SnuSugang, 1, progress), null);
        var actual = Project(stored, Online());

        Assert.Equal("Ui.SourceUnknown", actual.MessageKey);
        Assert.Null(actual.LastCompletedAtUtc);
        Assert.Null(actual.ActualStartedAtUtc);
        Assert.Null(actual.ReceivedAtUtc);
    }

    [Fact]
    public void CompletedRequestWithoutActualTransportRetainsCompletionButLeavesPhysicalFieldsUnknown()
    {
        var stored = Stored(SourceRequestOutcome.Unavailable, physicalEvidence: false);
        var actual = Project(stored, Online());

        Assert.Equal(Now.AddSeconds(-10), actual.LastCompletedAtUtc);
        Assert.Null(actual.ActualStartedAtUtc);
        Assert.Null(actual.ReceivedAtUtc);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(7, 23)]
    [InlineData(30, 0)]
    [InlineData(45, 0)]
    [InlineData(-7, 30)]
    public void CountdownSubtractsOnlyNonnegativeElapsedTimeAndNeverBecomesNegative(int elapsed, int expected)
    {
        var actual = Project(runtime: Online(), elapsed: TimeSpan.FromSeconds(elapsed));

        Assert.Equal(TimeSpan.FromSeconds(expected), actual.NextCheckIn);
    }

    [Fact]
    public void CountdownWithUnknownNegativeOrExtremeInputsIsBoundedWithoutOverflow()
    {
        Assert.Null(Project(runtime: new(true, false, null, null)).NextCheckIn);
        Assert.Equal(TimeSpan.Zero, Project(runtime: new(true, false, null, TimeSpan.FromSeconds(-1))).NextCheckIn);
        Assert.Equal(TimeSpan.Zero, Project(runtime: Online(), elapsed: TimeSpan.MaxValue).NextCheckIn);
        Assert.Equal(TimeSpan.FromSeconds(30), Project(runtime: Online(), elapsed: TimeSpan.MinValue).NextCheckIn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyExactStickyPolicyStorageFaultOffersRecoveryWhenControlsExist(bool controlsAvailable)
    {
        var actual = Project(Stored(SourceRequestOutcome.Success), Online(),
            Failure(RuntimeFailureKind.SavedState, "source_policy_storage_failed"), controlsAvailable: controlsAvailable);

        Assert.Equal("Ui.SavedStateError", actual.MessageKey);
        Assert.Equal(controlsAvailable, actual.CanRecoverPolicy);
        Assert.False(actual.CanResumeAccess);
    }

    [Fact]
    public void LocalReadFailureAloneDoesNotOfferPolicyRecovery()
    {
        var actual = Project(localSavedStateError: true);

        Assert.Equal("Ui.SavedStateError", actual.MessageKey);
        Assert.False(actual.CanRecoverPolicy);
    }

    private static ShellSourceStatus Project(SourceStatusSnapshot? stored = null, ShellRuntimeFacts? runtime = null,
        RuntimeFailure? failure = null, TimeSpan? elapsed = null, bool localSavedStateError = false, bool controlsAvailable = true) =>
        ShellSourceProjector.Project(stored, runtime, failure, Now, elapsed ?? TimeSpan.Zero, localSavedStateError, controlsAvailable);

    private static ShellRuntimeFacts Online(string? reason = null) => new(true, false, reason, TimeSpan.FromSeconds(30));

    private static RuntimeFailure Failure(RuntimeFailureKind kind, string code)
    {
        var hub = new RuntimeUpdateHub(TimeProvider.System);
        hub.ReportFailure(kind, code);
        return hub.Failure!;
    }

    private static SourceStatusSnapshot Stored(SourceRequestOutcome outcome, bool pause = false, TimeSpan? cooldown = null,
        DateTimeOffset? lastSuccess = null, bool physicalEvidence = true)
    {
        var successAt = lastSuccess ?? Now.AddMinutes(-2);
        var completedAt = outcome == SourceRequestOutcome.Success ? lastSuccess ?? Now.AddSeconds(-10) : Now.AddSeconds(-10);
        var progress = new SourceRequestProgress(3, Now.AddSeconds(-3), 2, outcome, completedAt,
            outcome == SourceRequestOutcome.Success ? 2 : 1, outcome == SourceRequestOutcome.Success ? completedAt : successAt);
        var policy = new SourcePolicySnapshot(SourcePolicyIds.SnuSugang, 3, progress,
            pause ? new(2, completedAt, "access_restricted") : null,
            cooldown is { } delay ? new(Now.AddSeconds(-1), delay < TimeSpan.Zero ? TimeSpan.Zero : delay + TimeSpan.FromSeconds(1)) : null);
        var evidence = physicalEvidence ? new CompletedTransportEvidence(2, Now.AddSeconds(-13), Now.AddSeconds(-11), completedAt) : null;
        return new(new(0, 0, Now), policy, evidence);
    }
}
