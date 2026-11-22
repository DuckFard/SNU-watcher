using System.Collections.Concurrent;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Navigation;
using SnuWatcher.Windows.Tests.Presentation.Common;

namespace SnuWatcher.Windows.Tests.Presentation.Alerts;

internal sealed class AlertFixture : IAsyncDisposable
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    internal DedicatedUiDispatcher Ui { get; } = new();
    internal HistoryReader Reader { get; } = new();
    internal RecordingActions Actions { get; } = new();
    internal ConcurrentQueue<string> Errors { get; } = new();
    internal AlertHistoryViewModel Model { get; }
    internal AlertFixture() => Model = new(Reader, Ui, new AlertClock(), Errors.Enqueue, Actions);
    public async ValueTask DisposeAsync() { await Model.DisposeAsync(); await Ui.DisposeAsync(); }
    internal static Guid Id(int value) => Guid.Parse(value.ToString("x32"));
    internal static SectionKey Key => new(2026, "U000200002", "U000300002", "F32.102", "012", "0");
    internal static UiReadStamp Stamp => new(1, 1000, Now);
    internal static AlertHistoryItem Item(int id = 1, long ordinal = 1000, DeliveryStatus status = DeliveryStatus.Pending) =>
        new(new(Id(id), Key, ordinal, 4, 0, Now.AddMinutes(-5), Now.AddMinutes(-4), 1, false, true),
            new(1, status, 0, null, null, null));
    internal static AlertHistoryPage Page(int id = 1, long ordinal = 1000, bool more = false) =>
        new(Stamp, 1000, [Item(id, ordinal)], more, more ? new(1000, ordinal, Id(id)) : null);

    private sealed class AlertClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}

internal sealed class HistoryReader : IAlertHistoryReader
{
    internal ConcurrentQueue<(AlertHistoryCursor? Cursor, int Size)> Pages { get; } = new();
    internal Func<AlertHistoryCursor?, CancellationToken, Task<AlertHistoryPage>> Page { get; set; } = (_, _) => Task.FromResult(AlertFixture.Page());
    internal Func<Guid, CancellationToken, Task<AlertHistoryItem?>> Details { get; set; } = (id, _) =>
    {
        var number = (int)Convert.ToInt64(id.ToString("N"), 16);
        return Task.FromResult<AlertHistoryItem?>(AlertFixture.Item(number, 1001 - number));
    };
    internal Func<Guid, AlertRecipientCursor?, CancellationToken, Task<AlertRecipientPage?>> Recipients { get; set; } =
        (id, _, _) => Task.FromResult<AlertRecipientPage?>(new(id, AlertFixture.Stamp, [], false, null));
    internal Func<Guid, AlertAttemptCursor?, CancellationToken, Task<AlertAttemptPage?>> Attempts { get; set; } =
        (id, _, _) => Task.FromResult<AlertAttemptPage?>(new(id, AlertFixture.Stamp, [], false, null));
    public Task<AlertHistoryPage> ReadPageAsync(AlertHistoryCursor? after, int pageSize, CancellationToken token)
    { Pages.Enqueue((after, pageSize)); return Page(after, token); }
    public Task<AlertHistoryItem?> ReadDetailsAsync(Guid id, CancellationToken token) => Details(id, token);
    public Task<AlertRecipientPage?> ReadRecipientsAsync(Guid id, AlertRecipientCursor? after, int pageSize, CancellationToken token) => Recipients(id, after, token);
    public Task<AlertAttemptPage?> ReadAttemptsAsync(Guid id, AlertAttemptCursor? after, int pageSize, CancellationToken token) => Attempts(id, after, token);
}

internal sealed class RecordingActions : IStudentActions
{
    internal StudentDestination? Opened { get; private set; }
    internal SectionKey? Copied { get; private set; }
    internal Func<SectionKey, CancellationToken, Task>? CopyHandler { get; set; }
    public Task OpenDestinationAsync(StudentDestination destination, CancellationToken token) { Opened = destination; return Task.CompletedTask; }
    public Task CopySectionIdentifierAsync(SectionKey key, CancellationToken token) { Copied = key; return CopyHandler?.Invoke(key, token) ?? Task.CompletedTask; }
}
