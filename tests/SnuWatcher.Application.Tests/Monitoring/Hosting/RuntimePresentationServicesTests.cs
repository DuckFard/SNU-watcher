using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class RuntimePresentationServicesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task MissingOptionalPresentationServicesFailWithFixedMessageWithoutStartingActors()
    {
        await using var fixture = new InteractiveRuntimeFixture();
        Assert.Same(fixture.Session, fixture.Session.Dashboard);
        Assert.Same(fixture.Session, fixture.Session.Preferences);
        Assert.Same(fixture.Session, fixture.Session.TermHints);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Preferences.ReadAsync());
        Assert.Equal("Presentation services are unavailable for this runtime.", error.Message);
        Assert.Null(fixture.Session.Failure);
        Assert.Equal(RuntimeSessionState.Created, fixture.Session.State);
        Assert.Equal(0, fixture.Recovery.Reads);
        Assert.Empty(fixture.Source.Calls);
    }

    [Fact]
    public async Task TypedFacadesForwardThroughSessionWithoutExposingInjectedPortsOrIssuingSourceRequests()
    {
        var ports = new RuntimePresentationPorts();
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        await fixture.Session.StartAsync();
        Assert.NotSame(ports, fixture.Session.Dashboard);
        var cursor = new WatchPageCursor(7, Guid.NewGuid());
        var page = await fixture.Session.Dashboard.ReadWatchPageAsync(cursor, 19);
        Assert.Equal(WatchPageReadDisposition.RestartRequired, page.Disposition);
        Assert.Equal(8, page.CurrentCatalogRevision);
        var id = Guid.NewGuid();
        Assert.Null(await fixture.Session.Dashboard.ReadWatchDetailsAsync(id));
        Assert.Equal(SourcePolicyIds.SnuSugang, (await fixture.Session.Dashboard.ReadSourceStatusAsync()).Policy.SourceId);
        Assert.Equal(0, (await fixture.Session.Preferences.ReadAsync()).Revision);
        Assert.Equal(0, (await fixture.Session.TermHints.ReadAsync()).Revision);
        Assert.Contains((cursor, 19), ports.Calls);
        Assert.Contains(id, ports.Calls);
        Assert.Empty(fixture.Source.Calls);
        Assert.Empty(fixture.Store.Issues);
        await fixture.Session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Preferences.ReadAsync());
    }

    [Fact]
    public async Task PresentationBoundaryRejectsInvalidPageOrIdentityWithoutFaultingOrCallingStore()
    {
        var ports = new RuntimePresentationPorts();
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        await fixture.Session.StartAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Session.Dashboard.ReadWatchPageAsync(pageSize: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Session.Dashboard.ReadWatchPageAsync(pageSize: 51));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Session.Dashboard.ReadWatchDetailsAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Session.Preferences.TrySaveAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Session.TermHints.TryReplaceYearAsync(null!));
        Assert.Empty(ports.Calls);
        Assert.Null(fixture.Session.Failure);
    }

    [Fact]
    public async Task CatalogCacheUsesRevisionCapturedBeforeQueryAndOriginalResultTimeWithoutRetryingConflict()
    {
        var ports = new RuntimePresentationPorts();
        ports.ReadTerms = _ => Task.FromResult(RuntimePresentationPorts.Terms(7));
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        var query = new CatalogQuery(2026);
        var original = ScriptedRuntimeCourseSource.CatalogResult(query, MonitorFixture.Now.AddDays(-1));
        fixture.Source.Catalog = (_, _) =>
        {
            Assert.Equal("terms_read", Assert.Single(ports.Calls));
            ports.ReadTerms = _ => Task.FromResult(RuntimePresentationPorts.Terms(8));
            return Task.FromResult(original);
        };
        await fixture.Session.StartAsync();
        Assert.Same(original, await fixture.Session.Courses.GetCatalogAsync(query));
        var write = Assert.Single(ports.Calls.OfType<ReplaceTermHintsCommand>());
        Assert.Equal(7, write.ExpectedCacheRevision);
        Assert.Equal(query.Year, write.Year);
        Assert.Equal(original.CompletedAtUtc, write.FetchedAtUtc);
        Assert.Equal<CatalogTerm>(original.Data!.Terms, write.Terms);
        Assert.Equal(1, ports.Calls.Count(call => call is string value && value == "terms_read"));
        Assert.Null(fixture.Session.Failure);
        Assert.Empty(fixture.Store.Issues);
        Assert.Empty(fixture.Channel.Envelopes);
    }

    [Fact]
    public async Task FailedCatalogNeverReplacesSavedHintsOrTurnsThemIntoLiveCatalogData()
    {
        var ports = new RuntimePresentationPorts();
        ports.ReadTerms = _ => Task.FromResult(RuntimePresentationPorts.Terms(7));
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        var failed = ScriptedRuntimeCourseSource.Failure<CourseCatalogData>();
        fixture.Source.Catalog = (_, _) => Task.FromResult(failed);
        await fixture.Session.StartAsync();
        Assert.Same(failed, await fixture.Session.Courses.GetCatalogAsync(new(2026)));
        Assert.Empty(ports.Calls.OfType<ReplaceTermHintsCommand>());
        Assert.Null(fixture.Session.Failure);
        Assert.Empty(fixture.Store.Issues);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedPresentationFailuresAreSanitizedSavedStateFaults(bool cacheWrite)
    {
        var ports = new RuntimePresentationPorts();
        if (cacheWrite) ports.Replace = (_, _) => throw new InvalidOperationException("private broken cache payload");
        else ports.ReadPreferences = _ => throw new IOException("private preferences path");
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        await fixture.Session.StartAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => cacheWrite
            ? fixture.Session.Courses.GetCatalogAsync(new(2026)) : (Task)fixture.Session.Preferences.ReadAsync());
        Assert.DoesNotContain("private", failure.Message);
        Assert.Null(failure.InnerException);
        var sticky = await fixture.Session.FailureTask.WaitAsync(Timeout);
        Assert.Equal(RuntimeFailureKind.SavedState, sticky.Kind);
        Assert.Equal("runtime_presentation_failed", sticky.Code);
        Assert.True((await fixture.Session.StopAsync().WaitAsync(Timeout)).IsFinalized);
    }

    [Fact]
    public async Task CatalogForDifferentQueryCannotBecomeCurrentDataOrReplaceOfflineHints()
    {
        var ports = new RuntimePresentationPorts();
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        fixture.Source.Catalog = (_, _) => Task.FromResult(ScriptedRuntimeCourseSource.CatalogResult(new(2025)));
        await fixture.Session.StartAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Courses.GetCatalogAsync(new(2026)));
        Assert.Equal("The runtime could not complete the source query.", error.Message);
        Assert.Null(error.InnerException);
        var failure = await fixture.Session.FailureTask.WaitAsync(Timeout);
        Assert.Equal(RuntimeFailureKind.Actor, failure.Kind);
        Assert.Equal("runtime_query_failed", failure.Code);
        Assert.Empty(ports.Calls.OfType<ReplaceTermHintsCommand>());
        Assert.Empty(fixture.Store.Issues);
        Assert.True((await fixture.Session.StopAsync()).IsFinalized);
    }

    [Fact]
    public async Task NullPresentationReadCannotBeReportedAsDefaultsOrHealthySavedState()
    {
        var ports = new RuntimePresentationPorts
        {
            ReadPreferences = _ => Task.FromResult<UiPreferencesSnapshot>(null!)
        };
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        await fixture.Session.StartAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Preferences.ReadAsync());
        Assert.Equal("The runtime could not access saved presentation data.", error.Message);
        Assert.Null(error.InnerException);
        var failure = await fixture.Session.FailureTask.WaitAsync(Timeout);
        Assert.Equal(RuntimeFailureKind.SavedState, failure.Kind);
        Assert.Equal("runtime_presentation_failed", failure.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Courses.GetCatalogAsync(new(2026)));
        Assert.Empty(fixture.Source.Calls);
        Assert.True((await fixture.Session.StopAsync()).IsFinalized);
    }

    [Fact]
    public async Task ExpectedSuccessfulCommandReplayConflictIsExplicitAndDoesNotFaultTheSession()
    {
        var ports = new RuntimePresentationPorts { Save = (_, _) => throw new UiPreferencesReplayConflictException() };
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        await fixture.Session.StartAsync();
        await Assert.ThrowsAsync<UiPreferencesReplayConflictException>(() => fixture.Session.Preferences.TrySaveAsync(
            new(Guid.NewGuid(), 0, UiPreferencesValues.Defaults)));
        Assert.Null(fixture.Session.Failure);
        Assert.Equal(RuntimeSessionState.Running, fixture.Session.State);
    }

    [Fact]
    public async Task CanceledUiSaveRetainsActualOperationAndShutdownDrainsItsLateReceipt()
    {
        var ports = new RuntimePresentationPorts();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<UiPreferencesSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ports.Save = (_, _) => { entered.TrySetResult(); return release.Task; };
        await using var fixture = new InteractiveRuntimeFixture(presentation: ports.Services);
        await fixture.Session.StartAsync();
        var command = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, UiPreferencesValues.Defaults);
        var saved = new UiPreferencesSnapshot(1, command.Values, MonitorFixture.Now);
        var receipt = new UiPreferencesSaveReceipt(command.CommandId, 0, saved);
        var committed = new UiPreferencesSaveResult(UiPreferencesSaveStatus.Applied, receipt, saved);
        using var caller = new CancellationTokenSource();
        var save = fixture.Session.Preferences.TrySaveAsync(command, caller.Token);
        Task<RuntimeSessionStopResult>? stopping = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            stopping = fixture.Session.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.Same(command, Assert.Single(ports.Calls.OfType<SaveUiPreferencesCommand>()));
        }
        finally { release.TrySetResult(committed); }
        Assert.True((await stopping!.WaitAsync(Timeout)).IsFinalized);
        Assert.Null(fixture.Session.Failure);
    }
}
