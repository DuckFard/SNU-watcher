using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Navigation;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>Only UTC is adjustable; real monotonic time/timers and the operating-system clock remain unchanged.</summary>
internal sealed class StudentUiValidationClock : TimeProvider
{
    private long _utcOffset;
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref _utcOffset));
    public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
    public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    internal void AdvanceUtc(TimeSpan duration) => Interlocked.Add(ref _utcOffset, duration.Ticks);
}

/// <summary>Explicit Demo source: no client, credentials, live endpoint or HTTP constructor exists in this fixture.</summary>
internal sealed class StudentUiValidationSource(StudentUiValidationClock clock) : ICourseSource, IAsyncDisposable
{
    internal const int Year = 2026;
    internal const string Selector = "U000200002U000300002";
    internal static SectionKey Key012 { get; } = new(Year, "U000200002", "U000300002", "F32.102", "012", "0");
    internal static SectionKey Key013 { get; } = new(Year, "U000200002", "U000300002", "F32.102", "013", "0");
    private int _catalogs, _interactive, _watch, _details, _disposed, _failInteractive, _holdDetails;
    private readonly TaskCompletionSource _releaseDetails = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource DetailsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int CatalogCalls => Volatile.Read(ref _catalogs);
    internal int InteractiveSearchCalls => Volatile.Read(ref _interactive);
    internal int WatchSearchCalls => Volatile.Read(ref _watch);
    internal int DetailsCalls => Volatile.Read(ref _details);
    internal int Calls => CatalogCalls + InteractiveSearchCalls + WatchSearchCalls + DetailsCalls;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal bool FailInteractiveSearch { set => Volatile.Write(ref _failInteractive, value ? 1 : 0); }
    internal void HoldDetails() => Volatile.Write(ref _holdDetails, 1);
    internal void ReleaseDetails() => _releaseDetails.TrySetResult();

    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken ct)
    {
        Check(ct); Interlocked.Increment(ref _catalogs);
        return Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
            new(query, [new(query.Year, Selector, "2학기 (Demo)", "Second semester (Demo)")],
                [new("DEMO.COLLEGE", "자연과학대학 (Demo)", "College of Natural Sciences (Demo)", "UP_DEPT"),
                 new("DEMO.DEPARTMENT", "통계학과 (Demo)", "Statistics (Demo)", "DEPT")]), [], clock.GetUtcNow()));
    }

    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken ct)
    {
        Check(ct);
        if (purpose == SourceRequestPurpose.Interactive) Interlocked.Increment(ref _interactive); else Interlocked.Increment(ref _watch);
        if (purpose == SourceRequestPurpose.Interactive && Volatile.Read(ref _failInteractive) != 0)
            return Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Failed, null,
                [new(SourceProblemKind.Unavailable, "ui_validation_source_unavailable")], clock.GetUtcNow()));
        ImmutableArray<CourseSearchRow> rows = query.Year == Year && query.TermSelector == Selector && query.CourseCode == Key012.CourseCode
            ? ImmutableArray.Create(Row(Key012, 40), Row(Key013, 39)) : [];
        return Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete, new(query, rows, 1), [], clock.GetUtcNow()));
    }

    public async Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken ct)
    {
        Check(ct); Interlocked.Increment(ref _details);
        if (Volatile.Read(ref _holdDetails) != 0)
        {
            DetailsEntered.TrySetResult();
            // The deliberate shutdown probe must retain the actual driver despite waiter cancellation.
            await _releaseDetails.Task.ConfigureAwait(false);
        }
        return new(SourceReadStatus.Complete, new(key, "통계학 (Demo)", "Statistics (Demo)", "이권상 (Demo)", "Demo instructor",
            "통계학과 (Demo)", 3, 45, "English / 한국어 (Demo)", "Demo information only; eligibility remains unconfirmed.",
            [new("월(14:00~15:15) 수(14:00~15:15)", "Lecture (Demo)", "25-101 (Demo)")]), [], clock.GetUtcNow());
    }

    private CourseSearchRow Row(SectionKey key, int registered) => new(key, "통계학 (Demo)", "이권상 (Demo)", "통계학과 (Demo)",
        "월(14:00~15:15) 수(14:00~15:15)", registered, 40, registered, "Enrollment/Quota", clock.GetUtcNow());
    private void Check(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(IsDisposed, this); }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }
}

/// <summary>Production delivery is disabled; any accidental call fails without acknowledging or using Windows APIs.</summary>
internal sealed class StudentUiValidationChannel : IBatchDesktopAlertChannel
{
    private int _calls;
    internal int Calls => Volatile.Read(ref _calls);
    public Task<DesktopAlertOutcome> SendAsync(BatchDeliveryEnvelope envelope, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromException<DesktopAlertOutcome>(new InvalidOperationException("ui_validation_delivery_must_be_disabled"));
    }
}

/// <summary>Records closed destination/identifier intent only; this class cannot open a browser or clipboard.</summary>
internal sealed class StudentUiValidationActions : IStudentActions
{
    private int _registration, _genie, _copies, _native;
    internal int RegistrationIntents => Volatile.Read(ref _registration);
    internal int GenieIntents => Volatile.Read(ref _genie);
    internal int CopyIntents => Volatile.Read(ref _copies);
    internal int NativeTestRequests => Volatile.Read(ref _native);
    public Task OpenDestinationAsync(StudentDestination destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (destination == StudentDestination.Registration) Interlocked.Increment(ref _registration);
        else if (destination == StudentDestination.Genie) Interlocked.Increment(ref _genie);
        else throw new ArgumentOutOfRangeException(nameof(destination));
        return Task.CompletedTask;
    }
    public Task CopySectionIdentifierAsync(SectionKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (key != StudentUiValidationSource.Key012) throw new InvalidOperationException("ui_validation_wrong_copy_identity");
        Interlocked.Increment(ref _copies);
        return Task.CompletedTask;
    }
    internal void NativeTestGuard()
    { Interlocked.Increment(ref _native); throw new InvalidOperationException("ui_validation_native_test_not_allowed"); }
}
