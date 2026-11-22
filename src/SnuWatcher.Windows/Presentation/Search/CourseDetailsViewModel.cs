using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Navigation;

namespace SnuWatcher.Windows.Presentation.Search;

/// <summary>One owned latest-selection details runner and fixed-destination desktop actions.</summary>
public sealed class CourseDetailsViewModel : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly IRuntimeCourseQueries queries;
    private readonly IUiDispatcher dispatcher;
    private readonly Action<string> reportError;
    private Request? requested;
    private CancellationTokenSource? active;
    private Task activeCancellation = Task.CompletedTask;
    private Task runner = Task.CompletedTask;
    private long generation;
    private bool running, closed;

    /// <summary>Creates an empty details controller. Queries use only exact source-confirmed full keys.</summary>
    public CourseDetailsViewModel(IRuntimeCourseQueries queries, IUiDispatcher dispatcher,
        IStudentActions? actions = null, Action<string>? reportError = null)
    {
        ArgumentNullException.ThrowIfNull(queries); ArgumentNullException.ThrowIfNull(dispatcher);
        this.queries = queries; this.dispatcher = dispatcher; this.reportError = reportError ?? (_ => { });
        Model = new(new(null, null, CourseUiLoadState.Idle), dispatcher, this.reportError);
        OpenRegistrationCommand = new(token => actions!.OpenDestinationAsync(StudentDestination.Registration, token), dispatcher,
            this.reportError, () => actions is not null);
        OpenGenieCommand = new(token => actions!.OpenDestinationAsync(StudentDestination.Genie, token), dispatcher,
            this.reportError, () => actions is not null);
        CopyIdentifierCommand = new(token => actions!.CopySectionIdentifierAsync(Model.State.Key!, token), dispatcher,
            this.reportError, () => actions is not null && Model.State.Key is not null);
    }
    /// <summary>Original details response and selected identity; no search counts are replaced by metadata capacity.</summary>
    public UiSnapshotModel<CourseDetailsSnapshot> Model { get; }
    /// <summary>Open only the fixed official registration homepage.</summary>
    public UiAsyncCommand OpenRegistrationCommand { get; }
    /// <summary>Open only the fixed public Genie homepage.</summary>
    public UiAsyncCommand OpenGenieCommand { get; }
    /// <summary>Copy the literal selected identifier through the desktop boundary.</summary>
    public UiAsyncCommand CopyIdentifierCommand { get; }

    /// <summary>
    /// Replaces at most one pending selection and cancels the obsolete gateway waiter. The single
    /// actual runner remains owned even if this caller cancels its await.
    /// </summary>
    public Task LoadAsync(SectionKey key, CancellationToken cancellationToken = default)
        => LoadConfirmedAsync(key, null, cancellationToken);

    internal Task LoadConfirmedAsync(SectionKey key, string? termName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        Task actual;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            requested = new(++generation, key, cancellationToken, termName);
            CancelActiveLocked();
            if (!running)
            {
                running = true;
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                runner = RunAsync(ready.Task);
                ready.SetResult();
            }
            actual = runner;
        }
        return actual.WaitAsync(cancellationToken);
    }

    private async Task RunAsync(Task ready)
    {
        try { await RunCoreAsync(ready).ConfigureAwait(false); }
        catch (Exception)
        {
            // A broken dispatcher may also reject failure publication. Close this read surface,
            // retain ownership, and let Dispose still drain every independently admitted action.
            lock (sync) { closed = true; requested = null; running = false; }
            Report("ui_details_publication_failed");
        }
    }

    private async Task RunCoreAsync(Task ready)
    {
        await ready.ConfigureAwait(false);
        while (true)
        {
            Request request;
            CancellationTokenSource cancellation;
            lock (sync)
            {
                if (closed || requested is null) { running = false; return; }
                request = requested;
                cancellation = new CancellationTokenSource();
                active = cancellation; activeCancellation = Task.CompletedTask;
            }
            var callerRegistration = request.Caller.Register(() =>
            {
                lock (sync) if (active == cancellation) CancelActiveLocked();
            });
            try
            {
                await PublishAsync(request.Generation, new(request.Key, null, CourseUiLoadState.Loading, request.TermName)).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                var read = await queries.GetDetailsAsync(request.Key, cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                if (read.Data is { } details && details.Key != request.Key) throw new InvalidOperationException("Unexpected details identity.");
                await PublishAsync(request.Generation, new(request.Key, read,
                    read.Status == SourceReadStatus.Complete ? CourseUiLoadState.Complete : CourseUiLoadState.Failed, request.TermName)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { await PublishAsync(request.Generation, new(request.Key, null, CourseUiLoadState.Canceled, request.TermName)).ConfigureAwait(false); }
            catch (Exception)
            {
                await PublishAsync(request.Generation, new(request.Key, null, CourseUiLoadState.Failed, request.TermName)).ConfigureAwait(false);
                Report("ui_details_failed");
            }
            finally
            {
                await callerRegistration.DisposeAsync().ConfigureAwait(false);
                Task canceled;
                lock (sync) { active = null; canceled = activeCancellation; }
                try { await canceled.ConfigureAwait(false); } catch (Exception) { Report("ui_details_cancel_failed"); }
                cancellation.Dispose();
            }
            lock (sync)
            {
                if (closed || requested is null || requested.Generation == request.Generation)
                { running = false; return; }
            }
        }
    }

    private Task PublishAsync(long version, CourseDetailsSnapshot state) => dispatcher.InvokeAsync(() =>
    {
        lock (sync)
        {
            if (closed || generation != version) return;
            Model.Replace(state);
            CopyIdentifierCommand.NotifyCanExecuteChanged();
        }
    });

    private void CancelActiveLocked()
    {
        if (active is null || active.IsCancellationRequested) return;
        // CancelAsync owns callbacks asynchronously; disposal waits for this captured task.
        try { activeCancellation = active.CancelAsync(); }
        catch (Exception) { Report("ui_details_cancel_failed"); }
    }

    /// <summary>Clears selection on the dispatcher and invalidates late responses without forgetting the runner.</summary>
    public void Clear()
    {
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Details selection requires the UI dispatcher.");
        lock (sync) { generation++; requested = null; CancelActiveLocked(); }
        Model.Replace(new(null, null, CourseUiLoadState.Idle));
        CopyIdentifierCommand.NotifyCanExecuteChanged();
    }
    /// <summary>Requests cancellation and publishes its terminal UI state on the dispatcher, retaining actual runner ownership.</summary>
    public void Cancel()
    {
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Details cancellation requires the UI dispatcher.");
        lock (sync) { generation++; requested = null; CancelActiveLocked(); }
        Model.Replace(Model.State with { Read = null, Status = Model.State.Key is null ? CourseUiLoadState.Idle : CourseUiLoadState.Canceled });
    }

    private void Report(string code) { try { reportError(code); } catch (Exception) { } }
    /// <summary>Closes selection admission and awaits the actual runner and desktop actions.</summary>
    public async ValueTask DisposeAsync()
    {
        Task actual;
        lock (sync) { closed = true; generation++; requested = null; CancelActiveLocked(); actual = runner; }
        try { await actual.ConfigureAwait(false); }
        catch (Exception) { Report("ui_details_publication_failed"); }
        await Task.WhenAll(OpenRegistrationCommand.DisposeAsync().AsTask(), OpenGenieCommand.DisposeAsync().AsTask(),
            CopyIdentifierCommand.DisposeAsync().AsTask()).ConfigureAwait(false);
    }
    private sealed record Request(long Generation, SectionKey Key, CancellationToken Caller, string? TermName);
}
