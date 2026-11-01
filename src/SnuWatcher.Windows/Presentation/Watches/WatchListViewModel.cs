using System.ComponentModel;
using System.Collections.Immutable;
using System.Diagnostics;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Watches;

/// <summary>Bounded watched-section presentation. Source and mutation authority remain in the runtime ports.</summary>
public sealed partial class WatchListViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IWatchDashboardReader reader;
    private readonly IRuntimeWatchCommands commands;
    private readonly IUiDispatcher dispatcher;
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan> appliedTarget;
    private readonly Func<AcademicTermIdentity, string?>? termLabel;
    private readonly UiAsyncCommand detailCommand;
    private readonly ImmutableArray<UiAsyncCommand> pageCommands;
    private readonly ImmutableArray<UiAsyncCommand> mutationCommands;
    private readonly ImmutableArray<UiAsyncCommand> allCommands;
    private readonly SemaphoreSlim pageGate = new(1, 1);
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly object disposalSync = new();
    private Navigation navigation = new(null, [], 1, false, null);
    private TaskCompletionSource<bool>? selectionReady;
    private long selectionGeneration;
    private long settledSelectionGeneration;
    private long successfulPageReadVersion;
    private long successfulReloadVersion;
    private volatile bool stopping;
    private Task? disposal;

    /// <summary>Creates a presentation over owned runtime facades and the currently applied refresh interval.</summary>
    public WatchListViewModel(IWatchDashboardReader reader, IRuntimeWatchCommands commands, IUiDispatcher dispatcher,
        TimeProvider clock, Func<TimeSpan> appliedTarget, Func<AcademicTermIdentity, string?>? termLabel = null)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(dispatcher); ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(appliedTarget);
        this.reader = reader; this.commands = commands; this.dispatcher = dispatcher;
        this.clock = clock; this.appliedTarget = appliedTarget; this.termLabel = termLabel;
        Model = new(new(), dispatcher);
        FirstCommand = PageCommand(PageMove.First);
        PreviousCommand = PageCommand(PageMove.Previous);
        NextCommand = PageCommand(PageMove.Next);
        ReloadCommand = PageCommand(PageMove.Reload);
        pageCommands = [FirstCommand, PreviousCommand, NextCommand, ReloadCommand];
        PauseCommand = MutationCommand(WatchEdit.Pause);
        ResumeCommand = MutationCommand(WatchEdit.Resume);
        RemoveCommand = MutationCommand(WatchEdit.Remove);
        RenameCommand = MutationCommand(WatchEdit.Rename);
        SaveExpiryCommand = MutationCommand(WatchEdit.Expiry);
        mutationCommands = [PauseCommand, ResumeCommand, RemoveCommand, RenameCommand, SaveExpiryCommand];
        RefreshSelectedMemberCommand = new(RefreshMemberCoreAsync, dispatcher, CommandFailure, CanRefresh);
        detailCommand = new(LoadSelectedDetailsCoreAsync, dispatcher, DetailFailure,
            () => !stopping && State.SelectedId is not null && selectionGeneration > settledSelectionGeneration);
        allCommands = [..pageCommands, ..mutationCommands, RefreshSelectedMemberCommand, detailCommand];
        foreach (var command in pageCommands.Concat(mutationCommands)) command.CanExecuteChanged += OnAvailabilityChanged;
        detailCommand.CanExecuteChanged += OnDetailAvailabilityChanged;
        Model.PropertyChanged += OnStateChanged;
    }
    /// <summary>Immutable state holder for WPF bindings.</summary>
    public UiSnapshotModel<WatchListState> Model { get; }
    /// <summary>Current immutable rendering snapshot.</summary>
    public WatchListState State => Model.State;
    /// <summary>Monotonic receipt for successful page reads published to the UI.</summary>
    public long SuccessfulPageReadVersion => Interlocked.Read(ref successfulPageReadVersion);
    /// <summary>Monotonic receipt for successful explicit reloads; navigation cannot advance it.</summary>
    public long SuccessfulReloadVersion => Interlocked.Read(ref successfulReloadVersion);
    /// <summary>Property changes are published on the dispatcher.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Visible selected row, or null for an off-page direct selection.</summary>
    public WatchRow? SelectedRow
    {
        get => State.Rows.FirstOrDefault(row => row.Id == State.SelectedId);
        set
        {
            if (value is null || State.SelectedId == value.Id || State.IsLoading || State.IsMutating
                || !State.Rows.Any(row => row.Id == value.Id)) return;
            RequestSelection(value.Id, allowDuringMutation: false);
        }
    }
    /// <summary>Exact selected member for manual checking.</summary>
    public WatchMemberRow? SelectedMember
    {
        get => State.Details?.Members.FirstOrDefault(member => member.Key == State.SelectedMemberKey);
        set
        {
            if (value is null || State.SelectedMemberKey == value.Key
                || State.Details?.Members.Any(member => member.Key == value.Key) != true) return;
            Model.Replace(State with { SelectedMemberKey = value.Key });
            RefreshSelectedMemberCommand.NotifyCanExecuteChanged();
        }
    }
    /// <summary>Label editor.</summary>
    public string RenameText { get => State.RenameText; set => Model.Replace(State with { RenameText = value ?? "", MessageKey = null }); }
    /// <summary>Whether expiry editing removes the boundary.</summary>
    public bool NoExpiry { get => State.NoExpiry; set => Model.Replace(State with { NoExpiry = value, MessageKey = null }); }
    /// <summary>Korea calendar date in the expiry editor.</summary>
    public DateTime? ExpiryDate { get => State.ExpiryDate; set => Model.Replace(State with { ExpiryDate = value, MessageKey = null }); }
    /// <summary>Loads the beginning without relying on retained backward positions.</summary>
    public UiAsyncCommand FirstCommand { get; }
    /// <summary>Loads a retained backward cursor.</summary>
    public UiAsyncCommand PreviousCommand { get; }
    /// <summary>Always advances the source's exact next cursor.</summary>
    public UiAsyncCommand NextCommand { get; }
    /// <summary>Reloads the current page, restarting on a changed definition epoch.</summary>
    public UiAsyncCommand ReloadCommand { get; }
    /// <summary>Pauses the selected exact definition revision.</summary>
    public UiAsyncCommand PauseCommand { get; }
    /// <summary>Resumes after expiry has been extended if necessary.</summary>
    public UiAsyncCommand ResumeCommand { get; }
    /// <summary>Removes the selected exact definition revision.</summary>
    public UiAsyncCommand RemoveCommand { get; }
    /// <summary>Saves the label editor against the selected revision.</summary>
    public UiAsyncCommand RenameCommand { get; }
    /// <summary>Saves inclusive Korea-date expiry against the selected revision.</summary>
    public UiAsyncCommand SaveExpiryCommand { get; }
    /// <summary>Requests the real source-gated refresh for one selected member.</summary>
    public UiAsyncCommand RefreshSelectedMemberCommand { get; }
    /// <summary>Loads one watch directly, including a newly created off-page watch.</summary>
    public async Task SelectWatchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("A watch identity is required.", nameof(id));
        if (!State.IsLoaded)
            await ReloadCommand.ExecuteAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? selected = null;
        await dispatcher.InvokeAsync(() => selected = RequestSelection(id, allowDuringMutation: false), cancellationToken).ConfigureAwait(false);
        await selected!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Reprojects existing facts with the current clock/profile; performs no I/O.</summary>
    public void RefreshAges()
    {
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Watch presentation updates require its dispatcher.");
        if (stopping) return;
        var now = clock.GetUtcNow();
        var target = appliedTarget();
        var page = Volatile.Read(ref navigation).Page;
        Model.Replace(State with
        {
            Rows = page is null ? [] : ProjectRows(page, now, target),
            Details = State.Details is null ? null : ProjectDetails(State.Details.Source, now, target),
        });
        NotifyCommands();
    }
    /// <summary>Stops and drains every owned presentation command.</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
        {
            if (disposal is not null) return new(disposal);
            disposal = DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Lifetime closure and cancellation must work even after the UI dispatcher has stopped.
        stopping = true;
        Volatile.Read(ref selectionReady)?.TrySetCanceled(new CancellationToken(true));
        foreach (var command in allCommands) command.Cancel();
        await Task.WhenAll(allCommands.Select(command => command.DisposeAsync().AsTask())).ConfigureAwait(false);
        Model.PropertyChanged -= OnStateChanged;
        foreach (var command in pageCommands.Concat(mutationCommands)) command.CanExecuteChanged -= OnAvailabilityChanged;
        detailCommand.CanExecuteChanged -= OnDetailAvailabilityChanged;
        pageGate.Dispose(); mutationGate.Dispose();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var property in new[] { nameof(State), nameof(SelectedRow), nameof(SelectedMember), nameof(RenameText), nameof(NoExpiry), nameof(ExpiryDate) })
            foreach (var observer in PropertyChanged?.GetInvocationList() ?? [])
                try { ((PropertyChangedEventHandler)observer)(this, new(property)); }
                catch (Exception) { Trace.WriteLine("ui_watch_notification_failed"); }
    }

    private void OnAvailabilityChanged(object? sender, EventArgs args)
    {
        if (stopping) return;
        var loading = pageCommands.Any(command => command.IsExecuting);
        var mutating = mutationCommands.Any(command => command.IsExecuting);
        if (State.IsLoading == loading && State.IsMutating == mutating) return;
        Model.Replace(State with { IsLoading = loading, IsMutating = mutating });
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        foreach (var command in allCommands) command.NotifyCanExecuteChanged();
    }

    private void SetMessage(string? resourceKey)
    {
        if (!stopping) Model.Replace(State with { MessageKey = resourceKey });
    }
    private void CommandFailure(string code) => SetMessage("Ui.CommandFailed");
}
