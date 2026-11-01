using SnuWatcher.Application.Monitoring.SourcePolicy;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.SourcePolicy;

public sealed class SourcePolicyResponseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Source = SourcePolicyIds.SnuSugang;

    [Fact]
    public void IssuedResponseMustMatchTheCurrentPolicyOrdinal()
    {
        var initial = SourcePolicySnapshot.Initial(Source);
        var issued = new IssuedSourceRequest(new(Guid.NewGuid(), Source, 1), SourceRequestKind.Detail, Now, 0);
        Assert.Throws<ArgumentException>(() => new SourceRequestIssueResult(initial, issued));
    }

    [Fact]
    public void ReceiptRejectsANegativePolicyRevision()
    {
        var token = new SourceRequestToken(Guid.NewGuid(), Source, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourcePolicyReceipt(token, true, -1));
    }

    [Fact]
    public void LongServerDelaySaturatesOnlyAtTheRepresentableUtcBoundary()
    {
        var cooldown = new SourceCooldown(DateTimeOffset.MaxValue.AddTicks(-1), TimeSpan.MaxValue);
        Assert.Equal(DateTimeOffset.MaxValue, cooldown.NotBeforeUtc);
        Assert.Equal(TimeSpan.MaxValue, cooldown.Delay);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceCooldown(Now, TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void ReducerRejectsResultIdentityThatDoesNotMatchItsIssuedIntent()
    {
        var issued = new IssuedSourceRequest(new(Guid.NewGuid(), Source, 1), SourceRequestKind.Catalog, Now, 0);
        var policy = new SourcePolicySnapshot(Source, 1, new SourceRequestProgress(1, Now, 0, null, null, 0, null));
        var different = new SourceRequestResultCommand(new(Guid.NewGuid(), Source, 1), SourceRequestOutcome.Success, Now);
        Assert.Throws<ArgumentException>(() => SourcePolicyReducer.Apply(policy, issued, different));
    }
}
