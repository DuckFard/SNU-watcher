using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Snu.Gateway;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed partial class SnuGatewayTests
{
    [Fact]
    public async Task MetadataCacheRetainsOriginalCompletionAndExpiresMonotonicallyAcrossUtcRollback()
    {
        await using var fixture = new Harness();
        var first = await fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
        fixture.Clock.AdjustUtc(TimeSpan.FromDays(-7));
        fixture.Clock.Advance(TimeSpan.FromMinutes(14));
        var cached = await fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Watch, default);
        Assert.Same(first, cached);
        Assert.Equal(Start, cached.CompletedAtUtc);
        Assert.Equal(1, fixture.Handler.Catalogs);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var fresh = await fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
        Assert.NotSame(first, fresh);
        Assert.Equal(fixture.Clock.GetUtcNow(), fresh.CompletedAtUtc);
        Assert.Equal(2, fixture.Handler.Catalogs);
    }

    [Fact]
    public async Task CatalogKeysRetainEveryFilterAndCapacityEvictsLeastRecentlyUsedEntry()
    {
        await using var fixture = new Harness(options: new(catalogCapacity: 2));
        var first = new CatalogQuery(2026);
        var second = new CatalogQuery(2026, upDepartment: "300");
        var third = new CatalogQuery(2026, Query.TermSelector, "300", "326");
        await fixture.Gateway.GetCatalogAsync(first, SourceRequestPurpose.Interactive, default);
        await fixture.Gateway.GetCatalogAsync(second, SourceRequestPurpose.Interactive, default);
        await fixture.Gateway.GetCatalogAsync(first, SourceRequestPurpose.Interactive, default);
        await fixture.Gateway.GetCatalogAsync(third, SourceRequestPurpose.Interactive, default);
        Assert.Equal(3, fixture.Handler.Catalogs);
        await fixture.Gateway.GetCatalogAsync(second, SourceRequestPurpose.Interactive, default);
        Assert.Equal(4, fixture.Handler.Catalogs);
    }

    [Fact]
    public async Task DetailCacheUsesFullSectionAndSubsubjectKeyAndSnapshotsMetadata()
    {
        await using var fixture = new Harness(options: new(detailsCapacity: 1));
        var first = await fixture.Gateway.GetDetailsAsync(Key, SourceRequestPurpose.Interactive, default);
        Assert.Same(first, await fixture.Gateway.GetDetailsAsync(Key, SourceRequestPurpose.Watch, default));
        Assert.Equal(1, fixture.Handler.Details);
        var other = new SectionKey(Key.Year, Key.SemesterCode, Key.DetailSemesterCode, Key.CourseCode, Key.Section, "001");
        Assert.Equal(other, (await fixture.Gateway.GetDetailsAsync(other, SourceRequestPurpose.Interactive, default)).Data!.Key);
        await fixture.Gateway.GetDetailsAsync(Key, SourceRequestPurpose.Interactive, default);
        Assert.Equal(3, fixture.Handler.Details);
        Assert.Equal("26-B102", first.Data!.Meetings[0].Room);
        Assert.Throws<NotSupportedException>(() => ((IList<SnuWatcher.Application.Sources.CourseMeeting>)first.Data.Meetings).Clear());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMetadataIsNotCachedAndRemoteValidationTextIsSanitized(bool invalidData)
    {
        await using var fixture = new Harness((request, number, token) => number == 1
            ? throw (invalidData ? (Exception)new InvalidDataException("Cookie: secret-session; raw response body")
                : new FormatException("Cookie: secret-session; raw response body")) : DefaultResponse(request, number, token));
        var failed = await fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
        Assert.Equal(SourceReadStatus.Failed, failed.Status);
        Assert.Null(failed.Data);
        Assert.Equal("source_parse_changed", Assert.Single(failed.Problems).DiagnosticCode);
        Assert.DoesNotContain("secret", failed.ToString(), StringComparison.Ordinal);
        var recovered = await fixture.Gateway.GetCatalogAsync(new(2026), SourceRequestPurpose.Interactive, default);
        Assert.Equal(SourceReadStatus.Complete, recovered.Status);
        Assert.Equal(2, fixture.Handler.Catalogs);
    }

    [Fact]
    public async Task UnconfirmedRawTermNeverDispatchesASearchOrSilentlyChoosesAnotherTerm()
    {
        await using var fixture = new Harness();
        var read = await fixture.Gateway.SearchAsync(new(2026, "unreportedTerm", "F32.102"), SourceRequestPurpose.Watch, default);
        Assert.Equal(SourceReadStatus.Failed, read.Status);
        Assert.Equal(SourceProblemKind.TermUnavailable, Assert.Single(read.Problems).Kind);
        Assert.Equal("term_unconfirmed", read.Problems[0].DiagnosticCode);
        Assert.Equal(1, fixture.Handler.Catalogs);
        Assert.Equal(0, fixture.Handler.Searches);
    }

    [Fact]
    public void OptionsAndDependenciesRejectUnboundedOrUnsupportedValues()
    {
        var defaults = new SnuGatewayOptions();
        Assert.Equal(16, defaults.CatalogCapacity);
        Assert.Equal(128, defaults.DetailsCapacity);
        Assert.Equal(TimeSpan.FromMinutes(15), defaults.MetadataTtl);
        Assert.Equal(128, defaults.MaxLiveFlights);
        Assert.Equal(256, defaults.MaxCallers);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuGatewayOptions(catalogCapacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuGatewayOptions(detailsCapacity: 4097));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuGatewayOptions(metadataTtl: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuGatewayOptions(metadataTtl: TimeSpan.FromDays(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuGatewayOptions(maxLiveFlights: 1025));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuGatewayOptions(maxCallers: 4097));
        Assert.Throws<ArgumentNullException>(() => new SnuCourseGateway(null!, TimeProvider.System));
    }
}
