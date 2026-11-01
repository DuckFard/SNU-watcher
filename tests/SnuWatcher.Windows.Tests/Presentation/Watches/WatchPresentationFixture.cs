using System.Collections.Concurrent;
using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Watches;
using SnuWatcher.Windows.Tests.Presentation.Common;

namespace SnuWatcher.Windows.Tests.Presentation.Watches;

internal sealed class WatchPresentationFixture : IWatchDashboardReader, IRuntimeWatchCommands, IAsyncDisposable
{
    private readonly object sync = new();
    private ImmutableDictionary<Guid, WatchDetailsSnapshot> data = ImmutableDictionary<Guid, WatchDetailsSnapshot>.Empty;
    internal WatchPresentationFixture()
    {
        ViewModel = new(this, this, Dispatcher, Clock, () => AppliedTarget, _ => "2학기");
    }
    internal DedicatedUiDispatcher Dispatcher { get; } = new();
    internal WatchClock Clock { get; } = new();
    internal WatchListViewModel ViewModel { get; }
    internal TimeSpan AppliedTarget { get; set; } = TimeSpan.FromSeconds(60);
    internal long Epoch { get; set; } = 1;
    internal int RowsPerPage { get; set; } = 25;
    internal int PageReads;
    internal int DetailReads;
    internal ConcurrentQueue<int> PageSizes { get; } = new();
    internal ConcurrentQueue<SubscriptionChange> Changes { get; } = new();
    internal ConcurrentQueue<SectionKey> Refreshes { get; } = new();
    internal Func<Guid, CancellationToken, Task<WatchDetailsSnapshot?>>? ReadDetails { get; set; }
    internal Func<WatchPageCursor?, CancellationToken, Task<WatchPageReadResult>>? ReadPage { get; set; }
    internal Func<SubscriptionChange, CancellationToken, Task<SubscriptionSnapshot>>? Change { get; set; }
    internal bool FailReads { get; set; }

    internal static Guid Id(int value) => Guid.ParseExact(value.ToString("x32"), "N");
    internal static SectionKey Key(string section = "012", string course = "F32.102") =>
        new(2026, "U000200002", "U000300001", course, section, "000");
    internal SectionSnapshot Current(SectionKey key, int enrolled = 39, int capacity = 40, TimeSpan? age = null)
    {
        var observed = Clock.GetUtcNow() - (age ?? TimeSpan.Zero);
        return new(key, 1, 1, CheckOutcome.Success, observed, observed,
            new(key, new(enrolled, capacity), QuotaMeaning.EnrolledOverQuota, 1, observed));
    }
    internal WatchDetailsSnapshot Add(int identity, string label = "통계학", WatchStatus status = WatchStatus.Active,
        WatchExpiry? expiry = null, params SectionSnapshot[] sections)
    {
        if (sections.Length == 0) sections = [Current(Key(identity.ToString("D3")))];
        var watch = new SubscriptionSnapshot(Id(identity), 1, 1, sections.Length == 1 ? SubscriptionKind.Section : SubscriptionKind.Group,
            label, status, true, expiry, Clock.GetUtcNow(), sections.Select(section => new MemberSnapshot(section.Key, 0, 1)));
        var details = new WatchDetailsSnapshot(new(Epoch, 1, Clock.GetUtcNow()), watch, sections);
        Put(details);
        return details;
    }
    internal void Put(WatchDetailsSnapshot details) { lock (sync) data = data.SetItem(details.Subscription.Id, details); }
    internal WatchDetailsSnapshot? Find(Guid id) { lock (sync) return data.GetValueOrDefault(id); }

    public Task<WatchPageReadResult> ReadWatchPageAsync(WatchPageCursor? after = null, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref PageReads); PageSizes.Enqueue(pageSize); cancellationToken.ThrowIfCancellationRequested();
        if (ReadPage is not null) return ReadPage(after, cancellationToken);
        if (FailReads) throw new IOException("private database path");
        lock (sync)
        {
            if (after is not null && after.CatalogRevision != Epoch)
                return Task.FromResult(new WatchPageReadResult(WatchPageReadDisposition.RestartRequired, Epoch, null));
            var remaining = data.Values.OrderBy(value => value.Subscription.Id.ToString("N"), StringComparer.Ordinal)
                .Where(value => after is null || string.CompareOrdinal(value.Subscription.Id.ToString("N"), after.AfterSubscriptionId.ToString("N")) > 0).ToArray();
            var selected = remaining.Take(Math.Min(pageSize, RowsPerPage)).ToArray();
            var rows = selected.Select(value =>
            {
                var watch = value.Subscription;
                return new WatchDefinitionSummary(watch.Id, watch.DefinitionRevision, watch.ActivationGeneration, watch.Kind,
                    watch.Label, watch.Status, watch.InitialNoticeEnabled, watch.Expiry, watch.CreatedAtUtc,
                    new(watch.Members[0].Key), watch.Members.Length, watch.Kind == SubscriptionKind.Section ? value.Sections[0] : null);
            }).ToArray();
            var more = remaining.Length > selected.Length;
            return Task.FromResult(new WatchPageReadResult(WatchPageReadDisposition.Page, Epoch,
                new(new(Epoch, 1, Clock.GetUtcNow()), rows, more, more ? new(Epoch, rows[^1].Id) : null)));
        }
    }
    public Task<WatchDetailsSnapshot?> ReadWatchDetailsAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref DetailReads); cancellationToken.ThrowIfCancellationRequested();
        if (FailReads) throw new IOException("private database path");
        return ReadDetails?.Invoke(subscriptionId, cancellationToken) ?? Task.FromResult(Find(subscriptionId));
    }
    public Task<SourceStatusSnapshot> ReadSourceStatusAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Watch rows must not read source policy per cell.");
    public Task<SubscriptionSnapshot> CreateAsync(NewSubscription command, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("This presentation does not create watches.");
    public Task<OwnedRefreshResult> RefreshAsync(SectionKey key, CancellationToken cancellationToken = default)
    {
        Refreshes.Enqueue(key);
        return Task.FromException<OwnedRefreshResult>(new InvalidOperationException("source policy remains authoritative"));
    }
    public Task<SubscriptionSnapshot> ChangeAsync(SubscriptionChange command, CancellationToken cancellationToken = default)
    {
        Changes.Enqueue(command);
        return Change?.Invoke(command, cancellationToken) ?? Task.FromResult(Apply(command));
    }
    internal SubscriptionSnapshot Apply(SubscriptionChange command)
    {
        lock (sync)
        {
            var existing = data[command.Id];
            var before = existing.Subscription;
            if (before.DefinitionRevision != command.ExpectedRevision) throw new StoreVersionConflictException();
            var status = command switch
            {
                PauseSubscription => WatchStatus.Paused, ResumeSubscription => WatchStatus.Active,
                RemoveSubscription => WatchStatus.Removed, _ => before.Status,
            };
            var updated = new SubscriptionSnapshot(before.Id, before.DefinitionRevision + 1,
                before.ActivationGeneration + (command is ResumeSubscription ? 1 : 0), before.Kind,
                command is RenameSubscription rename ? rename.Label : before.Label, status, before.InitialNoticeEnabled,
                command is SetSubscriptionExpiry expiry ? expiry.Expiry : before.Expiry, before.CreatedAtUtc, before.Members);
            Epoch++;
            data = status == WatchStatus.Removed ? data.Remove(command.Id)
                : data.SetItem(command.Id, new(new(Epoch, 1, Clock.GetUtcNow()), updated, existing.Sections));
            return updated;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await ViewModel.DisposeAsync();
        await Dispatcher.DisposeAsync();
    }
}

internal sealed class WatchClock : TimeProvider
{
    private long ticks = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
}
