using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;

namespace SnuWatcher.Integration.Tests.Persistence;

internal sealed class StoreFixture : IAsyncDisposable
{
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher.StoreTests"));
    private readonly string _directory = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));

    private StoreFixture() { }

    public StoreClock Clock { get; } = new();
    public SqliteWatchStore Store { get; private set; } = null!;
    public string DatabasePath => Path.Combine(_directory, "watches.sqlite3");

    public static SectionKey Key(string section = "012", string course = "F32.102") =>
        new(2026, "U000200002", "U000300002", course, section, "0");

    public static async Task<StoreFixture> CreateAsync(Action<SqliteStoreWriteStage>? faultInjector = null)
    {
        var fixture = new StoreFixture();
        Directory.CreateDirectory(fixture._directory);
        try
        {
            fixture.Store = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(fixture.DatabasePath), fixture.Clock, faultInjector);
            return fixture;
        }
        catch
        {
            fixture.DeleteDirectory();
            throw;
        }
    }

    public async Task ReopenAsync()
    {
        await Store.DisposeAsync();
        Store = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(DatabasePath), Clock);
    }

    public Task<SubscriptionSnapshot> AddAsync(SectionKey key, bool initialNotice = true, string label = "Statistics") =>
        Store.CreateSubscriptionAsync(NewSection(key, initialNotice, label), default);

    public NewSubscription NewSection(SectionKey key, bool initialNotice = true, string label = "Statistics", WatchExpiry? expiry = null) =>
        new(Guid.NewGuid(), label, SubscriptionKind.Section, [key], initialNotice, expiry, Clock.GetUtcNow());

    public Task<IssuedCheck> IssueAsync(params SectionKey[] keys) =>
        Store.IssueCheckAsync(new CheckIssue(Clock.GetUtcNow(), keys), default);

    public SectionCompletion Counts(IssuedCheck issued, SectionKey key, int registered, int capacity = 120,
        DateTimeOffset? observedAt = null) =>
        new(issued.Token, SeatObservation.Valid(key, issued.Token.Ordinal, observedAt ?? Clock.GetUtcNow(),
            new SeatCount(registered, capacity), QuotaMeaning.EnrolledOverQuota), CheckOutcome.Success);

    public SectionCompletion Unknown(IssuedCheck issued, SectionKey key) =>
        new(issued.Token, SeatObservation.Unknown(key, issued.Token.Ordinal, Clock.GetUtcNow()),
            CheckOutcome.Unavailable, "source_unavailable");

    public async Task<PreparedSectionCommit> PrepareAsync(SectionCompletion completion)
    {
        var context = await Store.ReadCommitContextAsync(completion.Token, completion.Key, default);
        return ObservationPreparer.Prepare(context, completion, Clock.GetUtcNow());
    }

    public async Task<TryCommitResult> CommitAsync(SectionCompletion completion) =>
        await Store.TryCommitSectionAsync(await PrepareAsync(completion), default);

    public async Task<TryCommitResult> ObserveAsync(SectionKey key, int registered, int capacity = 120)
    {
        var issued = await IssueAsync(key);
        return await CommitAsync(Counts(issued, key, registered, capacity));
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        DeleteDirectory();
    }

    private void DeleteDirectory()
    {
        var target = Path.GetFullPath(_directory);
        var parent = Path.GetDirectoryName(target);
        if (!string.Equals(parent, FixtureRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Refusing cleanup outside this fixture's unique temporary directory.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}

internal sealed class StoreClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset value) => _now = value.ToUniversalTime();
    public void Advance(TimeSpan interval) => _now = _now.Add(interval);
}
