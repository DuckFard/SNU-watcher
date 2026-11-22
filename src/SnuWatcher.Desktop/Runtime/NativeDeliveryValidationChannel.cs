using System.Threading;
using System.Threading.Tasks;
using SnuWatcher.Application.Delivery.Batches;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>Records the real channel's unchanged facts. It cannot fabricate readiness, acceptance or a native identifier.</summary>
internal sealed class NativeDeliveryValidationChannel(IBatchDesktopAlertChannel inner) : IBatchDesktopAlertChannel
{
    private int _calls, _opening;
    private readonly TaskCompletionSource _disabledOpening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls => Volatile.Read(ref _calls);
    internal BatchDesktopSubmission? Submission { get; private set; }
    internal BatchChannelResult? Result { get; private set; }
    internal Task DisabledOpeningRead => _disabledOpening.Task;
    internal void ObserveOpening() => Interlocked.Exchange(ref _opening, 1);
    public async Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken cancellationToken)
    {
        var result = await inner.ReadReadinessAsync(cancellationToken).ConfigureAwait(false);
        if (result == BatchChannelReadiness.Disabled && Volatile.Read(ref _opening) != 0) _disabledOpening.TrySetResult();
        return result;
    }
    public async Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Submission = submission;
        var result = await inner.SendAsync(submission, cancellationToken).ConfigureAwait(false);
        Result = result;
        return result;
    }
}
