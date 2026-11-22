using SnuWatcher.Application.Sources;

namespace SnuWatcher.Application.Monitoring.Scheduling;

public sealed partial class WatchMonitorLoop
{
    private sealed record ProfileControl(WatchMonitorProfileSnapshot Value,
        TaskCompletionSource<WatchMonitorProfileSnapshot> Acknowledgement);

    private WatchMonitorProfileSnapshot _appliedProfile;
    private ProfileControl _latestProfile;
    private ProfileControl? _pendingProfile;
    private ProfileControl? _applyingProfile;

    /// <summary>
    /// Apply saved intent in revision order. At most one pending control coalesces newer revisions;
    /// canceling a caller detaches its waiter without canceling accepted application.
    /// </summary>
    public Task<WatchMonitorProfileSnapshot> SetRefreshProfileAsync(WatchMonitorProfile profile,
        long preferencesRevision, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        ArgumentOutOfRangeException.ThrowIfNegative(preferencesRevision);
        cancellationToken.ThrowIfCancellationRequested();
        Task<WatchMonitorProfileSnapshot> task;
        lock (_sync)
        {
            if (_started == 0 || _finished) throw new InvalidOperationException("The monitor is not running.");
            RequireConsistentProfile(AppliedRefreshProfile, profile, preferencesRevision);
            RequireConsistentProfile(_applyingProfile?.Value, profile, preferencesRevision);
            RequireConsistentProfile(_pendingProfile?.Value, profile, preferencesRevision);
            if (preferencesRevision > _latestProfile.Value.PreferencesRevision)
            {
                var acknowledgement = _pendingProfile?.Acknowledgement
                    ?? new TaskCompletionSource<WatchMonitorProfileSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingProfile = new(new(profile, preferencesRevision), acknowledgement);
                _latestProfile = _pendingProfile;
            }
            task = _latestProfile.Acknowledgement.Task;
        }
        Signal();
        return task.WaitAsync(cancellationToken);
    }

    private static void RequireConsistentProfile(WatchMonitorProfileSnapshot? accepted,
        WatchMonitorProfile profile, long revision)
    {
        if (accepted?.PreferencesRevision == revision && accepted.Profile != profile)
            throw new WatchMonitorProfileConflictException();
    }

    private void ApplyPendingProfile()
    {
        ProfileControl? control;
        lock (_sync) { control = _pendingProfile; _pendingProfile = null; _applyingProfile = control; }
        if (control is null) return;
        var changed = _appliedProfile.Profile != control.Value.Profile;
        _appliedProfile = control.Value;
        if (!changed) return;
        foreach (var pair in _queries.Where(pair => pair.Value.LastStart is not null && !pair.Value.Requested))
            _queries = _queries.SetItem(pair.Key, pair.Value with { Due = AutomaticDue(pair.Key, pair.Value) });
    }

    private void AcknowledgeAppliedProfile()
    {
        lock (_sync)
        {
            _applyingProfile?.Acknowledgement.TrySetResult(AppliedRefreshProfile);
            _applyingProfile = null;
        }
    }

    // Called while admission is closed under _sync, before awaiting actual source drain.
    private void RejectUnappliedProfiles()
    {
        foreach (var control in new[] { _pendingProfile, _applyingProfile }.OfType<ProfileControl>())
        {
            control.Acknowledgement.TrySetException(new InvalidOperationException("The monitor is not running."));
            _ = control.Acknowledgement.Task.Exception; // Observe even if every caller detached.
        }
        _pendingProfile = null;
        _applyingProfile = null;
    }

    private TimeSpan AutomaticDue(CourseSearchQuery query, QueryEntry entry) => entry.LastStart is { } start
        ? Later(Add(start, Target(query, entry.Attempts)), entry.NotBefore) : entry.NotBefore;
}
