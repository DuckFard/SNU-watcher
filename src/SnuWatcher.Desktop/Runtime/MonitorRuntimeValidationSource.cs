using System;
using System.Threading;
using System.Threading.Tasks;
using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>Private fixture only: no HttpClient, source adapter, credentials, browser or native notification service.</summary>
internal sealed class MonitorRuntimeValidationSource : ICourseSource, IAsyncDisposable
{
    internal static SectionKey Key { get; } = new(2026, "U000200002", "U000300002", "F32.102", "012", "0");
    private int _calls, _disposed;
    internal int Calls => Volatile.Read(ref _calls);
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken ct)
    {
        Check(ct);
        return Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
            new(query, [new(query.Year, "U000200002U000300002", "Private validation term", null)], []), [], DateTimeOffset.UtcNow));
    }

    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken ct)
    {
        Check(ct);
        return Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete,
            new(query, [new(Key, "Private validation course", null, null, null, 119, 120, null,
                "Enrollment/Quota", DateTimeOffset.UtcNow)], 1), [], DateTimeOffset.UtcNow));
    }

    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken ct)
    {
        Check(ct);
        return Task.FromResult(new SourceRead<CourseDetails>(SourceReadStatus.Failed, null,
            [new(SourceProblemKind.Unavailable, "validation_details_unused")], DateTimeOffset.UtcNow));
    }

    private void Check(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(IsDisposed, this); Interlocked.Increment(ref _calls); }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }
}

/// <summary>Simulated acceptance is intentionally separate from any Windows API acceptance or visible banner.</summary>
internal sealed class MonitorRuntimeValidationChannel : IBatchDesktopAlertChannel
{
    private int _calls;
    internal int Calls => Volatile.Read(ref _calls);
    internal TaskCompletionSource<BatchDeliveryEnvelope> Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<DesktopAlertOutcome> SendAsync(BatchDeliveryEnvelope envelope, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Invoked.TrySetResult(envelope);
        return Task.FromResult(DesktopAlertOutcome.OsAccepted);
    }
}
