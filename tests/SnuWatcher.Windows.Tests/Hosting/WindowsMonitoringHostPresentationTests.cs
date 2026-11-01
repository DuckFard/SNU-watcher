using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Windows.Hosting;
using Xunit;

namespace SnuWatcher.Windows.Tests.Hosting;

public sealed class WindowsMonitoringHostPresentationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "012", "0");

    [Fact]
    public async Task SavedPreferenceProfileAndRevisionAreAppliedAtStartupWithoutSourceReads()
    {
        using var fixture = new RuntimeProfileFixture();
        var saved = await SeedPreferencesAsync(fixture.Profile);
        var source = new PresentationSource();
        var opened = await WindowsMonitoringHost.OpenValidationAsync(fixture.Profile, () => source, new HostTestChannel());
        await using var host = opened.Host!;

        Assert.Equal(WatchMonitorProfile.Faster, host.RefreshProfile.AppliedRefreshProfile.Profile);
        Assert.Equal(saved.Revision, host.RefreshProfile.AppliedRefreshProfile.PreferencesRevision);
        Assert.Equal(0, source.Calls);
        var preferences = await host.Preferences.ReadAsync(default);
        Assert.Equal(UiLanguage.Korean, preferences.Values.Language);
        Assert.Equal(saved.Revision, preferences.Revision);
    }

    [Fact]
    public async Task InvalidSavedPreferencesAbortBeforeSourceFactoryAndReleaseTheOwner()
    {
        using var fixture = new RuntimeProfileFixture();
        await using (var store = await SqliteWatchStore.OpenAsync(new(fixture.Profile.DatabasePath)))
        using (var connection = new SqliteConnectionFactory(fixture.Profile.DatabasePath).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO UiPreferences(id,revision,savedUtc,valuesJson) VALUES(1,1,$at,$values);";
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$values", "{\"version\":999}");
            command.ExecuteNonQuery();
        }
        var sources = 0;
        SqliteWatchStore? openedStore = null;
        var factories = new WindowsMonitoringHostFactories(async (profile, clock, ct) =>
        {
            openedStore = await SqliteWatchStore.OpenAsync(new(profile.DatabasePath), clock, cancellationToken: ct);
            return openedStore;
        }, (_, _, _) =>
        {
            Interlocked.Increment(ref sources);
            return Task.FromResult(WindowsMonitoringHostSource.FromFake(new PresentationSource()));
        });
        WindowsHostOpenResult? opened = null;
        try
        {
            var failure = await Record.ExceptionAsync(async () => opened = await WindowsMonitoringHost.OpenForTestingAsync(
                fixture.Profile, new HostTestChannel(), factories));
            Assert.NotNull(failure);
            Assert.Equal(0, sources);
            Assert.NotNull(openedStore);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => openedStore!.ListSubscriptionsAsync(default));
            var next = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
            Assert.Equal(DatabaseOwnerDisposition.Acquired, next.Disposition);
            await next.Lease!.DisposeAsync();
        }
        finally { if (opened?.Host is { } host) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task TypedFacadesAreOwnedSessionViewsAndOnlyQueriesCallTheSharedSource()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new PresentationSource();
        var opened = await WindowsMonitoringHost.OpenValidationAsync(fixture.Profile, () => source, new HostTestChannel());
        await using var host = opened.Host!;

        Assert.Same(host.Session, host.Courses);
        Assert.Same(host.Session, host.Dashboard);
        Assert.Same(host.Session, host.Preferences);
        Assert.Same(host.Session, host.TermHints);
        Assert.Same(host.Session, host.WatchCommands);
        Assert.Same(host.Session, host.RefreshProfile);
        Assert.NotSame(source, host.Courses);
        Assert.Equal(UiLanguage.English, (await host.Preferences.ReadAsync(default)).Values.Language);
        Assert.Empty((await host.TermHints.ReadAsync(default)).Years);
        Assert.Empty((await host.Dashboard.ReadWatchPageAsync(null, 25, default)).Page!.Items);
        Assert.Equal(0, source.Calls);

        var catalog = await host.Courses.GetCatalogAsync(new CatalogQuery(2026), default);
        Assert.Equal(SourceReadStatus.Complete, catalog.Status);
        Assert.Equal(1, source.Calls);
        Assert.Equal(SourceRequestPurpose.Interactive, source.LastPurpose);
        Assert.Single((await host.TermHints.ReadAsync(default)).Years);
        Assert.True((await host.StopAsync()).IsFinalized);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Preferences.ReadAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Courses.GetCatalogAsync(new CatalogQuery(2026), default));
    }

    [Fact]
    public async Task CanceledQueryWaiterDoesNotReleaseTheOwnerBeforeActualSourceWorkDrains()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new PresentationSource(blockCatalog: true);
        var opened = await WindowsMonitoringHost.OpenValidationAsync(fixture.Profile, () => source, new HostTestChannel());
        var host = opened.Host!;
        try
        {
            using var waiter = new CancellationTokenSource();
            var read = host.Courses.GetCatalogAsync(new CatalogQuery(2026), waiter.Token);
            await source.CatalogStarted.Task.WaitAsync(Deadline);
            waiter.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            var stopping = host.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.False(source.Disposed);
            var contender = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
            Assert.Equal(DatabaseOwnerDisposition.AlreadyOwned, contender.Disposition);
            source.ReleaseCatalog.TrySetResult();
            Assert.True((await stopping.WaitAsync(Deadline)).IsFinalized);
            Assert.True(source.Disposed);
            Assert.Equal(1, source.Calls);
        }
        finally { source.ReleaseCatalog.TrySetResult(); await host.DisposeAsync(); }
    }

    [Fact]
    public async Task MonitoringOnlyFactoryOverridesCallerDeliveryAndRetainsPendingAlertsWithoutAcknowledging()
    {
        using var fixture = new RuntimeProfileFixture();
        var source = new PresentationSource();
        SqliteWatchStore? store = null;
        var factories = new WindowsMonitoringHostFactories(async (profile, clock, ct) =>
        {
            store = await SqliteWatchStore.OpenAsync(new(profile.DatabasePath), clock, cancellationToken: ct);
            return store;
        }, (_, _, _) => Task.FromResult(WindowsMonitoringHostSource.FromFake(source)));
        Func<RuntimeSessionOptions?, TimeProvider?, CancellationToken, Task<WindowsHostOpenResult>> productionFactory =
            WindowsMonitoringHost.OpenMonitoringOnlyProductionAsync;
        Assert.NotNull(productionFactory); // Verify the closed production signature without opening its normal profile.
        var options = new RuntimeSessionOptions(deliveryEnabled: true);
        var opened = await WindowsMonitoringHost.OpenMonitoringOnlyForTestingAsync(fixture.Profile, factories, options: options);
        await using var host = opened.Host!;
        Assert.False(host.NotifyDeliveryChannelChanged());
        Assert.True(options.DeliveryEnabled);
        await host.WatchCommands.CreateAsync(new NewSubscription(Guid.NewGuid(), "Private monitoring-only watch", SubscriptionKind.Section,
            [Key], true, null, DateTimeOffset.UtcNow));
        using var deadline = new CancellationTokenSource(Deadline);
        while ((await store!.ReadPendingDeliveriesAsync(1, deadline.Token)).IsEmpty) await Task.Delay(10, deadline.Token);
        var pending = Assert.Single(await store.ReadPendingDeliveriesAsync(1));
        Assert.Equal(DeliveryStatus.Pending, pending.Status);
        Assert.Equal(0, pending.Attempts);
        Assert.Equal(WindowsMonitoringHostState.Running, host.State);
        Assert.Null(host.Session.Failure);
        Assert.True((await host.StopAsync()).IsFinalized);
        var ownership = await DatabaseOwnerGate.TryAcquireAsync(fixture.Profile.ApplicationId);
        Assert.Equal(DatabaseOwnerDisposition.Acquired, ownership.Disposition);
        await using var owner = ownership.Lease!;
        await using var verify = await SqliteWatchStore.OpenAsync(new(fixture.Profile.DatabasePath));
        var retained = Assert.Single(await verify.ReadPendingDeliveriesAsync(1));
        Assert.Equal(pending.EventId, retained.EventId);
        Assert.Equal(0, retained.Attempts);
    }

    private static async Task<UiPreferencesSnapshot> SeedPreferencesAsync(RuntimeProfile profile)
    {
        await using var store = await SqliteWatchStore.OpenAsync(new(profile.DatabasePath));
        var values = new UiPreferencesValues(UiLanguage.Korean, 2026,
            new AcademicTermHint(2026, "U000200002U000300002"), WatchMonitorProfile.Faster,
            UiCloseBehavior.MinimizeToTray, 3);
        var result = await ((IUiPreferencesStore)store).TrySaveAsync(new SaveUiPreferencesCommand(Guid.NewGuid(), 0, values), default);
        return result.CurrentSnapshot;
    }

    private sealed class PresentationSource(bool blockCatalog = false) : ICourseSource, IAsyncDisposable
    {
        public int Calls;
        public bool Disposed;
        public SourceRequestPurpose LastPurpose;
        public TaskCompletionSource CatalogStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCatalog { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); LastPurpose = purpose; CatalogStarted.TrySetResult();
            if (blockCatalog) await ReleaseCatalog.Task;
            return new(SourceReadStatus.Complete, new(query,
                [new CatalogTerm(query.Year, "U000200002U000300002", "Private validation term", null)], []), [], DateTimeOffset.UtcNow);
        }
        public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); LastPurpose = purpose;
            return Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete, new(query,
                [new(Key, "Private validation course", null, null, null, 119, 120, null, "Enrollment/Quota", DateTimeOffset.UtcNow)], 1), [], DateTimeOffset.UtcNow));
        }
        public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); LastPurpose = purpose;
            return Task.FromResult(new SourceRead<CourseDetails>(SourceReadStatus.Failed, null,
                [new(SourceProblemKind.Unavailable, "validation_details_unused")], DateTimeOffset.UtcNow));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
