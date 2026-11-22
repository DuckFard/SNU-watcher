using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Snu;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuStoredPolicyProvenanceTests
{
    [Fact]
    public async Task EachPageRetainsItsOwnActualHeaderAndBodyTimesAcrossSlowStorageAndLaterRequests()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        var stored = 0;
        fixture.Probe.HoldRecord = () =>
        {
            if (Interlocked.Increment(ref stored) == 1) fixture.Clock.Advance(TimeSpan.FromSeconds(50));
            return Task.CompletedTask;
        };
        await using var source = await fixture.SourceAsync((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ClockedContent(StoredPolicyFixture.Fixture(fixture.HttpCalls == 1 ? "search-page1.html" : "search-page2.html"), fixture.Clock) }));
        var result = await fixture.AfterStartupAsync(source.SearchAsync(new(new(2026, "U000200002U000300001", "2학기"), CourseCode: "F32.102")));
        var origin = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(origin.AddSeconds(5), result.Sections.Single(r => r.Identity.Section == "001").ObservedAtUtc);
        Assert.Equal(origin.AddSeconds(63), result.Sections.Single(r => r.Identity.Section == "012").ObservedAtUtc);
        Assert.Equal(origin.AddSeconds(68), result.CompletedAtUtc);
        Assert.Equal(2, fixture.Probe.Commands.Count);
        var first = fixture.Probe.Commands[0];
        var second = fixture.Probe.Commands[1];
        Assert.Equal(origin.AddSeconds(2), first.ActualStartedAtUtc);
        Assert.Equal(origin.AddSeconds(2), first.ReceivedAtUtc);
        Assert.Equal(origin.AddSeconds(10), first.CompletedAtUtc);
        Assert.Equal(origin.AddSeconds(60), second.ActualStartedAtUtc);
        Assert.Equal(origin.AddSeconds(60), second.ReceivedAtUtc);
        Assert.Equal(origin.AddSeconds(68), second.CompletedAtUtc);
        Assert.Equal(new long[] { 1, 2 }, fixture.Probe.Commands.Select(c => c.Token.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http503RecordsItsDistinctOutcomeIncludingTheNoHeaderCase(bool retryAfter)
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        await using var source = await fixture.SourceAsync((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (retryAfter) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(2));
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<SnuSourceException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        var command = Assert.Single(fixture.Probe.Commands);
        Assert.Equal(SourceRequestOutcome.ServiceUnavailable, command.Outcome);
        var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang);
        Assert.Equal(retryAfter, policy.Cooldown is not null);
        Assert.Equal(retryAfter, command.RetryAfter is not null);
        Assert.Equal(command.ActualStartedAtUtc, command.ReceivedAtUtc);
    }

    [Fact]
    public async Task FailedPersistenceOfAKnown403KeepsBothStorageFaultAndAccessPauseUntilTheirSeparateRecovery()
    {
        await using var fixture = await StoredPolicyFixture.CreateAsync();
        fixture.Probe.FailRecordBefore = true;
        await using var source = await fixture.SourceAsync((_, _) => Task.FromResult(fixture.HttpCalls == 1
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) : StoredPolicyFixture.Json()));
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.AfterStartupAsync(source.GetCatalogAsync(2026)));
        Assert.Equal(SourceRequestOutcome.AccessRestricted, Assert.Single(fixture.Probe.Commands).Outcome);
        await Assert.ThrowsAnyAsync<IOException>(() => source.ResumeSourceAccessAsync(0));
        Assert.Equal(0, fixture.Probe.ResumeCalls);
        fixture.Probe.FailRecordBefore = false;
        await source.RecoverSourcePolicyAsync();
        Assert.Null(source.SourcePolicyDiagnosticCode);
        await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
        Assert.Equal(1, fixture.HttpCalls);
        var policy = await fixture.Store.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang);
        Assert.NotNull(policy.AccessPause);
        Assert.True((await source.ResumeSourceAccessAsync(policy.Revision)).Applied);
        await source.GetCatalogAsync(2026);
        Assert.Equal(2, fixture.HttpCalls);
    }

    private sealed class ClockedContent : HttpContent
    {
        private readonly Stream stream;
        public ClockedContent(string text, QueueClock clock)
        { stream = new ClockedStream(Encoding.UTF8.GetBytes(text), clock); Headers.ContentType = new("text/html"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
    }

    private sealed class ClockedStream(byte[] bytes, QueueClock clock) : MemoryStream(bytes)
    {
        private bool received;
        private bool disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = base.Read(buffer.Span);
            if (count == 0 && !received) { received = true; clock.Advance(TimeSpan.FromSeconds(3)); }
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed) { disposed = true; clock.Advance(TimeSpan.FromSeconds(5)); }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
