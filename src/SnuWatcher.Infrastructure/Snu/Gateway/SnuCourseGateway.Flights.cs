using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Snu.Gateway;

public sealed partial class SnuCourseGateway
{
    private readonly Dictionary<CatalogQuery, Flight<CourseCatalogData>> _catalogFlights = [];
    private readonly Dictionary<SectionKey, Flight<CourseDetails>> _detailFlights = [];
    private readonly Dictionary<CourseSearchQuery, List<Flight<CourseSearchData>>> _searchFlights = [];
    private readonly HashSet<Flight> _allFlights = [];
    private int _running;

    private abstract class Flight
    {
        public CancellationTokenSource Stop { get; } = new();
        public CancellationToken Token { get; }
        public TaskCompletionSource Drained { get; } = NewSignal();
        public int Waiters { get; set; }
        public bool ActualComplete { get; set; }
        public bool StopRequested { get; set; }
        public bool CancelRunning { get; set; }
        public bool Disposing { get; set; }
        public bool Joinable => !ActualComplete && !StopRequested;
        protected Flight() => Token = Stop.Token;
    }

    private sealed class Flight<T>(Action<Flight<T>, SourceRead<T>?> finish) : Flight where T : class
    {
        public TaskCompletionSource<SourceRead<T>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<Flight<T>, SourceRead<T>?> Finish { get; } = finish;
    }

    private Task<SourceRead<T>> MetadataAsync<TKey, T>(TKey key, MetadataCache<TKey, T> cache,
        Dictionary<TKey, Flight<T>> flights, Func<CancellationToken, Task<SourceRead<T>>> operation,
        CancellationToken token) where TKey : notnull where T : class
    {
        Flight<T> flight;
        var created = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            token.ThrowIfCancellationRequested();
            if (cache.TryGet(key, out var cached)) return Task.FromResult(cached!);
            if (!flights.TryGetValue(key, out flight!) || !flight.Joinable)
            {
                if (_running >= _options.MaxLiveFlights) return Task.FromResult(Busy<T>());
                flight = new((completed, read) =>
                {
                    if (flights.TryGetValue(key, out var current) && ReferenceEquals(current, completed)) flights.Remove(key);
                    if (!_closed && !completed.StopRequested && read?.Status == SourceReadStatus.Complete) cache.Set(key, read);
                });
                flights[key] = flight;
                RegisterFlight(flight);
                created = true;
            }
            flight.Waiters++;
        }
        return WaitAndStart(flight, created, operation, token);
    }

    private Task<SourceRead<CourseSearchData>> SearchFlightAsync(CourseSearchQuery query, SourceRequestPurpose purpose,
        Func<CancellationToken, Task<SourceRead<CourseSearchData>>> operation, CancellationToken token)
    {
        Flight<CourseSearchData>? flight;
        var created = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            token.ThrowIfCancellationRequested();
            _searchFlights.TryGetValue(query, out var matches);
            // Watch freshness comes from a new operation, never from the UTC age of an existing UI response.
            flight = purpose == SourceRequestPurpose.Interactive ? matches?.LastOrDefault(item => item.Joinable) : null;
            if (flight is null)
            {
                if (_running >= _options.MaxLiveFlights) return Task.FromResult(Busy<CourseSearchData>());
                flight = new((completed, _) =>
                {
                    var list = _searchFlights[query];
                    list.Remove(completed);
                    if (list.Count == 0) _searchFlights.Remove(query);
                });
                if (matches is null) _searchFlights[query] = matches = [];
                matches.Add(flight);
                RegisterFlight(flight);
                created = true;
            }
            flight.Waiters++;
        }
        return WaitAndStart(flight, created, operation, token);
    }

    private void RegisterFlight(Flight flight)
    {
        _allFlights.Add(flight);
        _running++;
    }

    private Task<SourceRead<T>> WaitAndStart<T>(Flight<T> flight, bool created,
        Func<CancellationToken, Task<SourceRead<T>>> operation, CancellationToken token) where T : class
    {
        var waiter = AwaitFlightAsync(flight, token);
        if (created) _ = RunFlightAsync(flight, operation);
        return waiter;
    }

    private async Task<SourceRead<T>> AwaitFlightAsync<T>(Flight<T> flight, CancellationToken token) where T : class
    {
        try { return await flight.Result.Task.WaitAsync(token).ConfigureAwait(false); }
        finally { Detach(flight); }
    }

    private async Task RunFlightAsync<T>(Flight<T> flight, Func<CancellationToken, Task<SourceRead<T>>> operation) where T : class
    {
        SourceRead<T>? read = null;
        Exception? failure = null;
        try { read = await GuardSourceAsync(operation, flight.Token).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        bool cleanup;
        lock (_sync)
        {
            flight.ActualComplete = true;
            _running--;
            flight.Finish(flight, read);
            cleanup = ReserveCleanup(flight);
        }
        if (flight.StopRequested || failure is OperationCanceledException) flight.Result.TrySetCanceled(flight.Token);
        else if (failure is not null) flight.Result.TrySetException(failure);
        else flight.Result.TrySetResult(read!);
        if (cleanup) DisposeFlight(flight);
    }
}
