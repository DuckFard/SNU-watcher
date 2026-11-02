using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Windows.Notifications;

namespace SnuWatcher.Windows.Presentation.Settings;

public sealed partial class SettingsViewModel
{
    private readonly INativeNotificationControls? nativeControls;
    private readonly SemaphoreSlim nativeLane = new(1, 1);

    /// <summary>Records a student report only for the current accepted test; stale identities cannot attach to a newer request.</summary>
    public bool ConfirmNativeTest(Guid testId, NativeTestBannerConfirmation confirmation)
    {
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Native test confirmation requires the UI dispatcher.");
        if (!Enum.IsDefined(confirmation)) throw new ArgumentOutOfRangeException(nameof(confirmation));
        var state = Model.State;
        if (closed || state.IsNativeBusy || state.NativeTestReceipt is not { } receipt
            || receipt.TestId != testId || receipt.Result.Outcome != BatchChannelOutcome.OsAccepted) return false;
        Model.Replace(state with { NativeTestFeedback = new(testId, confirmation) });
        return true;
    }

    private async Task RunNativeSerializedAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        await nativeLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            ThrowIfClosed();
            await UpdateAsync(state => state with { IsNativeBusy = true }).ConfigureAwait(false);
            ThrowIfClosed();
            await operation(token).ConfigureAwait(false);
        }
        finally
        {
            try { await UpdateAsync(state => state with { IsNativeBusy = false }).ConfigureAwait(false); }
            finally { nativeLane.Release(); }
        }
    }

    private async Task ReadNativeStatusCoreAsync(CancellationToken token)
    {
        try
        {
            var status = await nativeControls!.ReadStatusAsync(token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(status);
            await UpdateAsync(state => state with { NativeStatus = status,
                NativeIssueKey = state.NativeTestReceipt is { } receipt ? NativeTestResultKey(receipt.Result.Outcome) : null }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await UpdateAsync(state => state with { NativeStatus = null, NativeIssueKey = "Ui.NativeStatusUnavailable" }).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SendNativeTestCoreAsync(CancellationToken token)
    {
        await UpdateAsync(state => state with { NativeTestReceipt = null, NativeTestFeedback = null,
            NativeIssueKey = "Ui.NativeTestPending" }).ConfigureAwait(false);
        NativeNotificationTestReceipt receipt;
        try
        {
            receipt = await nativeControls!.SendTestAsync(token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(receipt);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await UpdateAsync(state => state with { NativeIssueKey = "Ui.NativeTestCanceled" }).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await UpdateAsync(state => state with { NativeIssueKey = "Ui.NativeTestFailed" }).ConfigureAwait(false);
            throw;
        }
        await UpdateAsync(state => state with { NativeTestReceipt = receipt,
            NativeTestFeedback = receipt.Result.Outcome == BatchChannelOutcome.OsAccepted
                ? new(receipt.TestId, NativeTestBannerConfirmation.Unknown) : null,
            NativeIssueKey = NativeTestResultKey(receipt.Result.Outcome) }).ConfigureAwait(false);
        if (token.IsCancellationRequested) return; // A known acceptance survives late caller cancellation.
        try { await ReadNativeStatusCoreAsync(token).ConfigureAwait(false); }
        catch (Exception) { /* The probe has its own visible status; it cannot erase an actual test receipt. */ }
    }

    private static string NativeTestResultKey(BatchChannelOutcome outcome) => outcome switch
    {
        BatchChannelOutcome.OsAccepted => "Ui.NativeTestAccepted",
        BatchChannelOutcome.NotSubmitted => "Ui.NativeTestNotSubmitted",
        BatchChannelOutcome.RetryableFailure => "Ui.NativeTestRejected",
        _ => "Ui.NativeTestUnknown",
    };

    private async Task DisposeNativeLaneAsync(Task commandDrain)
    {
        try { await commandDrain.ConfigureAwait(false); }
        finally { nativeLane.Dispose(); }
    }
}
