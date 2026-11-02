using System.Collections.Immutable;
using System.ComponentModel;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Search;

/// <summary>Explicit same-term confirmation, with one stable create command retained across ambiguous retries.</summary>
public sealed class WatchSelectionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IRuntimeWatchCommands watches;
    private readonly IUiDispatcher dispatcher;
    private readonly TimeProvider clock;
    private readonly Action<string> reportError;
    private NewSubscription? pending;

    /// <summary>Creates a confirmation surface without submitting or selecting any section.</summary>
    public WatchSelectionViewModel(IRuntimeWatchCommands watches, IUiDispatcher dispatcher, TimeProvider clock,
        Action<string>? reportError = null)
    {
        ArgumentNullException.ThrowIfNull(watches); ArgumentNullException.ThrowIfNull(dispatcher); ArgumentNullException.ThrowIfNull(clock);
        this.watches = watches; this.dispatcher = dispatcher; this.clock = clock; this.reportError = reportError ?? (_ => { });
        Model = new(new([], "", true, false, null, WatchSelectionStatus.Empty, null, null, true), dispatcher, this.reportError);
        Model.PropertyChanged += (_, _) => NotifyBindings();
        SaveCommand = new(SaveAsync, dispatcher, this.reportError,
            () => !Model.State.Rows.IsEmpty && Model.State.Status != WatchSelectionStatus.Saved);
    }
    /// <summary>Immutable confirmed rows, input values and actual receipt state.</summary>
    public UiSnapshotModel<WatchSelectionSnapshot> Model { get; }
    /// <summary>Submits one immutable command; retries retain its identity and facts after an ambiguous failure.</summary>
    public UiAsyncCommand SaveCommand { get; }
    /// <summary>User-editable bounded watch label, frozen once a create may have been admitted.</summary>
    public string Label { get => Model.State.Label; set => Edit(state => state with { Label = value ?? "" }); }
    /// <summary>Creation-time notice preference; it does not change another watch's initialized state.</summary>
    public bool InitialNoticeEnabled { get => Model.State.InitialNoticeEnabled; set => Edit(state => state with { InitialNoticeEnabled = value }); }
    /// <summary>Whether the selected Korean local date is included as the final active day.</summary>
    public bool UseExpiry { get => Model.State.UseExpiry; set => Edit(state => state with { UseExpiry = value }); }
    /// <summary>Date-only input. Host timezone and DateTime.Kind never define the stored boundary.</summary>
    public DateTime? ExpiryDate { get => Model.State.ExpiryDate; set => Edit(state => state with { ExpiryDate = value }); }
    /// <summary>Binding changes are published only on the UI dispatcher.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Actual returned receipt, published after state replacement; observer failure cannot undo it.</summary>
    public event Action<SubscriptionSnapshot>? WatchCreated;

    // Only the search controller can supply source-confirmed rows; no public arbitrary-row creation API.
    internal void ReplaceSelection(ImmutableArray<CourseSearchRow> rows, string? termName = null)
    {
        RequireUi();
        if (!Model.State.CanEdit) return;
        if (rows.IsDefault || rows.Length > PersistenceLimits.MaxSections || rows.Any(row => row is null)
            || rows.DistinctBy(row => row.Key).Count() != rows.Length)
            throw new ArgumentException("Selection must be initialized, bounded and unique.", nameof(rows));
        var first = rows.FirstOrDefault();
        if (first is not null && rows.Any(row => row.Key.Year != first.Key.Year || row.Key.SemesterCode != first.Key.SemesterCode
            || row.Key.DetailSemesterCode != first.Key.DetailSemesterCode))
            throw new ArgumentException("Selected sections must belong to one full academic term.", nameof(rows));
        pending = null;
        var label = first is null ? "" : new string(first.Name.Where(character => !char.IsControl(character)).Take(PersistenceLimits.MaxLabelLength).ToArray()).Trim();
        if (first is not null && string.IsNullOrWhiteSpace(label)) label = first.Key.ToString();
        Model.Replace(new(rows, label, true, false, null, rows.IsEmpty ? WatchSelectionStatus.Empty : WatchSelectionStatus.Ready,
            null, null, true, termName));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Edit(Func<WatchSelectionSnapshot, WatchSelectionSnapshot> change)
    {
        RequireUi();
        if (!Model.State.CanEdit || Model.State.Status == WatchSelectionStatus.Saved) return;
        Model.Replace(change(Model.State) with { IssueKey = null, Status = Model.State.Rows.IsEmpty ? WatchSelectionStatus.Empty : WatchSelectionStatus.Ready });
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        NewSubscription? command = null;
        await dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                pending ??= BuildCommand(Model.State);
                command = pending;
                Model.Replace(Model.State with { Status = WatchSelectionStatus.Saving, IssueKey = null, CanEdit = false });
            }
            catch (ArgumentException)
            { Model.Replace(Model.State with { Status = WatchSelectionStatus.Invalid, IssueKey = Model.State.UseExpiry ? "Ui.ExpiryInvalid" : "Ui.FiltersInvalid" }); }
        }, cancellationToken).ConfigureAwait(false);
        if (command is null) return;
        try
        {
            // Once admitted, cancellation of a view/command cannot hide a committed creation receipt.
            var receipt = await watches.CreateAsync(command, CancellationToken.None).ConfigureAwait(false);
            if (receipt.Id != command.Id) throw new InvalidOperationException("Unexpected creation identity.");
            await dispatcher.InvokeAsync(() =>
            {
                Model.Replace(Model.State with { Status = WatchSelectionStatus.Saved, Created = receipt, IssueKey = null, CanEdit = true });
                foreach (var observer in WatchCreated?.GetInvocationList() ?? [])
                {
                    try { ((Action<SubscriptionSnapshot>)observer)(receipt); }
                    catch (Exception) { Report("ui_watch_observer_failed"); }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await dispatcher.InvokeAsync(() => Model.Replace(Model.State with
                { Status = WatchSelectionStatus.RetryRequired, CanEdit = false, IssueKey = "Ui.SavePending" })).ConfigureAwait(false);
            Report("ui_watch_save_failed");
        }
    }

    private NewSubscription BuildCommand(WatchSelectionSnapshot state)
    {
        if (state.Rows.IsEmpty) throw new ArgumentException("Select sections before confirmation.");
        WatchExpiry? expiry = null;
        if (state.UseExpiry)
        {
            if (state.ExpiryDate is not { } selectedDate) throw new ArgumentException("An expiry date is required.");
            expiry = WatchExpiry.FromLocalDate(DateOnly.FromDateTime(selectedDate));
            if (expiry.IsExpiredAt(clock.GetUtcNow())) throw new ArgumentException("The selected final day has already ended.");
        }
        return new(Guid.NewGuid(), state.Label, state.Rows.Length == 1 ? SubscriptionKind.Section : SubscriptionKind.Group,
            state.Rows.Select(row => row.Key), state.InitialNoticeEnabled, expiry, clock.GetUtcNow());
    }

    private void RequireUi()
    { if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Selection editing requires the UI dispatcher."); }
    private void NotifyBindings()
    {
        foreach (var observer in PropertyChanged?.GetInvocationList() ?? [])
        {
            try { ((PropertyChangedEventHandler)observer)(this, new(string.Empty)); }
            catch (Exception) { Report("ui_state_notification_failed"); }
        }
    }
    private void Report(string code) { try { reportError(code); } catch (Exception) { } }
    /// <summary>Closes command admission and awaits an actual admitted create and its UI publication.</summary>
    public ValueTask DisposeAsync() => SaveCommand.DisposeAsync();
}
