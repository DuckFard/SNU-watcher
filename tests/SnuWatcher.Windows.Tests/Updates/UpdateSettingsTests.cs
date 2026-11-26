using System.Net;
using System.Text.Json;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Updates;
using Xunit;

namespace SnuWatcher.Windows.Tests.Updates;

public sealed class UpdateSettingsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 3, 0, 0, TimeSpan.Zero);

    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text, out var version) ? version! : throw new ArgumentException(text);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store : IUpdateCheckStore
    {
        public bool Enabled = true;
        public DateTimeOffset? LastChecked;
        public string? NewestSeen;
        public bool FailWrites;
        public bool FailReads;
        public bool ReadEnabled() => FailReads ? throw new UnauthorizedAccessException() : Enabled;
        public void WriteEnabled(bool enabled) { if (FailWrites) throw new IOException(); Enabled = enabled; }
        public DateTimeOffset? ReadLastChecked() => FailReads ? throw new IOException() : LastChecked;
        public string? ReadNewestSeen() => FailReads ? throw new IOException() : NewestSeen;
        public void WriteChecked(DateTimeOffset checkedAtUtc, string? newestSeen)
        { if (FailWrites) throw new IOException(); LastChecked = checkedAtUtc; NewestSeen = newestSeen; }
    }

    private sealed class Source : IUpdateSource
    {
        public int Calls;
        public Func<ReleaseVersion?> Answer = () => null;
        public Task<ReleaseVersion?> GetNewestAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(Answer()); }
    }

    private readonly Clock clock = new();
    private readonly Store store = new();
    private readonly Source source = new();
    private readonly List<Uri> opened = [];

    private UpdateSettings Create(string current = "0.1.0-preview.3") => new(store, source, V(current), clock, opened.Add);

    [Fact]
    public async Task ANewerPublishedVersionIsOfferedAndItsReleasePageOpens()
    {
        source.Answer = () => V("0.1.0-preview.4");
        var updates = Create();
        var changed = new List<string?>();
        updates.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await updates.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.Equal("0.1.0-preview.4", updates.AvailableVersion);
        Assert.True(updates.ShowNotice);
        Assert.Contains(nameof(UpdateSettings.ShowNotice), changed);
        Assert.Equal((Start, "0.1.0-preview.4"), (store.LastChecked, store.NewestSeen));
        updates.OpenReleasePage();
        Assert.Equal("https://github.com/DuckFard/SNU-watcher/releases/tag/v0.1.0-preview.4", Assert.Single(opened).AbsoluteUri);
    }

    [Theory]
    [InlineData("0.1.0-preview.3")]
    [InlineData("0.1.0-preview.2")]
    [InlineData(null)]
    public async Task TheSameOrAnOlderVersionIsNotOffered(string? newest)
    {
        source.Answer = () => newest is null ? null : V(newest);
        var updates = Create();

        await updates.CheckIfDueAsync(CancellationToken.None);

        Assert.Null(updates.Available);
        Assert.False(updates.ShowNotice);
        updates.OpenReleasePage();
        Assert.Empty(opened);
        Assert.Equal(Start, store.LastChecked);
    }

    [Fact]
    public async Task ASuccessfulCheckIsNotRepeatedWithinADay()
    {
        var updates = Create();
        await updates.CheckIfDueAsync(CancellationToken.None);
        clock.Now = Start + UpdateSettings.CheckInterval - TimeSpan.FromMinutes(1);
        await updates.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(1, source.Calls);

        clock.Now = Start + UpdateSettings.CheckInterval;
        await updates.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task ARestartWithinADayOfTheLastCheckDoesNotAskAgainButStillShowsWhatItFound()
    {
        store.LastChecked = Start - TimeSpan.FromHours(3);
        store.NewestSeen = "0.1.0-preview.4";
        var updates = Create();

        Assert.Equal("0.1.0-preview.4", updates.AvailableVersion);
        await updates.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task AfterUpgradingTheRememberedVersionIsNoLongerOffered()
    {
        store.LastChecked = Start - TimeSpan.FromHours(3);
        store.NewestSeen = "0.1.0-preview.4";

        Assert.Null(Create(current: "0.1.0-preview.4").Available);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(JsonException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task AFailedCheckIsSilentAndRetriedAfterAnHour(Type failure)
    {
        source.Answer = () => throw (Exception)Activator.CreateInstance(failure)!;
        var updates = Create();

        await updates.CheckIfDueAsync(CancellationToken.None);
        clock.Now = Start + UpdateSettings.RetryInterval - TimeSpan.FromSeconds(1);
        await updates.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(1, source.Calls);
        Assert.Null(updates.Available);
        Assert.Null(updates.IssueKey);
        Assert.Null(store.LastChecked);

        source.Answer = () => V("0.1.0-preview.4");
        clock.Now = Start + UpdateSettings.RetryInterval;
        await updates.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(2, source.Calls);
        Assert.Equal("0.1.0-preview.4", updates.AvailableVersion);
    }

    [Fact]
    public async Task ALastCheckInTheFutureDoesNotBlockChecking()
    {
        store.LastChecked = Start + TimeSpan.FromDays(30);
        var updates = Create();

        await updates.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task TurningTheSwitchOffStopsChecksAndHidesTheOfferAndItIsRemembered()
    {
        source.Answer = () => V("0.1.0-preview.4");
        var updates = Create();
        await updates.CheckIfDueAsync(CancellationToken.None);

        updates.CheckForUpdates = false;
        clock.Now = Start + TimeSpan.FromDays(2);
        await updates.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.Null(updates.Available);
        Assert.False(store.Enabled);
        Assert.False(Create().CheckForUpdates);
        Assert.Null(Create().Available);
    }

    [Fact]
    public void TheSwitchStartsOnAndShowsAFailedChange()
    {
        var updates = Create();
        Assert.True(updates.CheckForUpdates);

        store.FailWrites = true;
        updates.CheckForUpdates = false;

        Assert.True(updates.CheckForUpdates);
        Assert.Equal("Ui.UpdateSettingFailed", updates.IssueKey);
    }

    [Fact]
    public async Task AnUnreadableStoreFallsBackToCheckingAndAFailedRecordStillOffersTheUpdate()
    {
        store.FailReads = true;
        store.FailWrites = true;
        source.Answer = () => V("0.1.0-preview.4");
        var updates = Create();

        Assert.True(updates.CheckForUpdates);
        await updates.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal("0.1.0-preview.4", updates.AvailableVersion);
    }

    [Fact]
    public async Task DismissingHidesTheNoticeUntilANewerVersionAppears()
    {
        source.Answer = () => V("0.1.0-preview.4");
        var updates = Create();
        await updates.CheckIfDueAsync(CancellationToken.None);

        updates.Dismiss();
        Assert.False(updates.ShowNotice);
        Assert.Equal("0.1.0-preview.4", updates.AvailableVersion);

        source.Answer = () => V("0.1.0-preview.5");
        clock.Now = Start + UpdateSettings.CheckInterval;
        await updates.CheckIfDueAsync(CancellationToken.None);
        Assert.True(updates.ShowNotice);
    }

    [Fact]
    public async Task OnlyOneCheckRunsAtATime()
    {
        var gate = new TaskCompletionSource<ReleaseVersion?>();
        var slow = new SlowSource(gate.Task);
        var updates = new UpdateSettings(store, slow, V("0.1.0-preview.3"), clock, opened.Add);

        var first = updates.CheckIfDueAsync(CancellationToken.None);
        clock.Now = Start + TimeSpan.FromDays(3);
        await updates.CheckIfDueAsync(CancellationToken.None);
        gate.SetResult(null);
        await first;

        Assert.Equal(1, slow.Calls);
    }

    private sealed class SlowSource(Task<ReleaseVersion?> answer) : IUpdateSource
    {
        public int Calls;
        public Task<ReleaseVersion?> GetNewestAsync(CancellationToken cancellationToken) { Calls++; return answer; }
    }

    [Fact]
    public void ArgumentsAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateSettings(null!, source, V("1.0.0"), clock, opened.Add));
        Assert.Throws<ArgumentNullException>(() => new UpdateSettings(store, null!, V("1.0.0"), clock, opened.Add));
        Assert.Throws<ArgumentNullException>(() => new UpdateSettings(store, source, null!, clock, opened.Add));
        Assert.Throws<ArgumentNullException>(() => new UpdateSettings(store, source, V("1.0.0"), null!, opened.Add));
        Assert.Throws<ArgumentNullException>(() => new UpdateSettings(store, source, V("1.0.0"), clock, null!));
    }
}
