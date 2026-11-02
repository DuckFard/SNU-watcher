using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Planning;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Scheduling;

public sealed partial class WatchMonitorLoop
{
    private async Task<CommitProgress> FetchAndCommitAsync(PlannedWatchQuery plan, CancellationToken cancellationToken)
    {
        var issued = ImmutableArray.CreateBuilder<IssuedCheck>();
        foreach (var scope in plan.IssueScopes)
            issued.Add(await _store.IssueCheckAsync(new(_clock.GetUtcNow(), scope), cancellationToken).ConfigureAwait(false));
        var policy = await _policyStore.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang, cancellationToken).ConfigureAwait(false);
        if (policy.IsBlockedAt(_clock.GetUtcNow())) throw new MonitorSourceDeferredException();
        cancellationToken.ThrowIfCancellationRequested();
        var read = await _source.SearchAsync(plan.Query, SourceRequestPurpose.Watch, cancellationToken).ConfigureAwait(false);
        var receivedAt = MonotonicNow;
        cancellationToken.ThrowIfCancellationRequested();
        var facts = issued.SelectMany(attempt => SourceCompletionMapper.Map(attempt, plan.Query, read)).ToImmutableArray();
        var parse = facts.Any(fact => fact.Outcome == CheckOutcome.ParseChanged)
            || read.Problems.Any(problem => problem.Kind is SourceProblemKind.ParseChanged or SourceProblemKind.ResponseTooLarge);
        var outcome = new AttemptOutcome(read.Status != SourceReadStatus.Complete || parse, parse,
            read.Problems.Where(problem => problem.RetryAfter is not null).Select(problem => problem.RetryAfter).Max(), receivedAt);
        var progress = await CommitAsync(facts, cancellationToken).ConfigureAwait(false);
        return progress with { Outcome = outcome };
    }

    private async Task<CommitProgress> CommitAsync(ImmutableArray<SectionCompletion> facts, CancellationToken cancellationToken)
    {
        var pending = ImmutableArray.CreateBuilder<SectionCompletion>();
        var committed = ImmutableArray.CreateBuilder<SectionKey>();
        foreach (var completion in facts)
        {
            var accepted = false;
            for (var attempt = 0; attempt < _options.MaxCommitAttempts; attempt++)
            {
                var context = await _store.ReadCommitContextAsync(completion.Token, completion.Key, cancellationToken).ConfigureAwait(false);
                var prepared = ObservationPreparer.Prepare(context, completion, _clock.GetUtcNow());
                var result = await _store.TryCommitSectionAsync(prepared, cancellationToken).ConfigureAwait(false);
                if (result.Status == CommitWriteStatus.VersionConflict) continue;
                accepted = true;
                break;
            }
            if (accepted) committed.Add(completion.Key); else pending.Add(completion);
        }
        return new(pending.ToImmutable(), committed.ToImmutable());
    }

    private async Task<T> SignalWhenCompleteAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        finally { Signal(); }
    }
}
