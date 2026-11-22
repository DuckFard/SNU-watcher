using System.Net;
using System.Text;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Infrastructure.Snu;

namespace SnuWatcher.Integration.Tests;

internal sealed class StoredPolicyFixture : IAsyncDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher.StoredPolicyTests"));
    private readonly string directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
    private StoredPolicyFixture() => Probe = new(() => (ISourcePolicyStore)Store);
    public QueueClock Clock { get; } = new();
    public SqliteWatchStore Store { get; private set; } = null!;
    public PolicyProbe Probe { get; }
    public int HttpCalls;
    public string DatabasePath => Path.Combine(directory, "watch.sqlite3");

    public static async Task<StoredPolicyFixture> CreateAsync()
    {
        var fixture = new StoredPolicyFixture();
        Directory.CreateDirectory(fixture.directory);
        fixture.Store = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(fixture.DatabasePath), fixture.Clock);
        return fixture;
    }

    public async Task ReopenAsync()
    {
        await Store.DisposeAsync();
        Store = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(DatabasePath), Clock);
    }

    public Task<SnuCourseSource> SourceAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        Func<Task>? afterScopeReleased = null) => SnuCourseSource.CreateStoredForTestingAsync(new HttpClient(new StoredHandler(async (request, token) =>
        {
            Interlocked.Increment(ref HttpCalls);
            return send is null ? Json() : await send(request, token);
        })), Probe, new(TimeSpan.Zero), Clock, afterScopeReleased);

    public async Task<T> AfterStartupAsync<T>(Task<T> operation)
    {
        await Clock.NextTimerAsync(TimeSpan.FromSeconds(2));
        Clock.Advance(TimeSpan.FromSeconds(2));
        return await operation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public static HttpResponseMessage Json() => new(HttpStatusCode.OK)
    { Content = new StringContent(Fixture("catalog.json"), Encoding.UTF8, "application/json") };
    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    public static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        var target = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(target), Root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) throw new InvalidOperationException("Unsafe fixture cleanup.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}

internal sealed class StoredHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public bool Disposed { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await send(request, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }
    protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
}

internal sealed class PolicyProbe(Func<ISourcePolicyStore> inner) : ISourcePolicyStore
{
    public bool FailIssue { get; set; }
    public bool FailRead { get; set; }
    public bool FailRecordBefore { get; set; }
    public bool FailRecordAfter { get; set; }
    public Action? AfterIssue { get; set; }
    public Func<Task>? HoldRecord { get; set; }
    public Func<Task>? HoldResume { get; set; }
    public List<SourceRequestResultCommand> Commands { get; } = [];
    public List<bool> RecordTokensCancelable { get; } = [];
    public int ResumeCalls { get; private set; }
    public int ReadCalls { get; private set; }
    public Task<SourcePolicySnapshot> ReadSourcePolicyAsync(string sourceId, CancellationToken cancellationToken)
    {
        ReadCalls++;
        return FailRead ? Task.FromException<SourcePolicySnapshot>(new IOException("private storage path")) : inner().ReadSourcePolicyAsync(sourceId, cancellationToken);
    }
    public async Task<SourceRequestIssueResult> TryIssueSourceRequestAsync(SourceRequestIssue command, CancellationToken cancellationToken)
    {
        if (FailIssue) throw new IOException("private storage path");
        var issued = await inner().TryIssueSourceRequestAsync(command, cancellationToken);
        AfterIssue?.Invoke();
        return issued;
    }
    public async Task<SourcePolicyReceipt> RecordSourceRequestResultAsync(SourceRequestResultCommand command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        RecordTokensCancelable.Add(cancellationToken.CanBeCanceled);
        if (HoldRecord is not null) await HoldRecord();
        if (FailRecordBefore) throw new IOException("private storage path");
        var result = await inner().RecordSourceRequestResultAsync(command, cancellationToken);
        if (FailRecordAfter) throw new IOException("private acknowledgement path");
        return result;
    }
    public async Task<SourceAccessResumeResult> ResumeSourceAccessAsync(SourceAccessResumeCommand command, CancellationToken cancellationToken)
    {
        ResumeCalls++;
        if (HoldResume is not null) await HoldResume();
        return await inner().ResumeSourceAccessAsync(command, cancellationToken);
    }
}
