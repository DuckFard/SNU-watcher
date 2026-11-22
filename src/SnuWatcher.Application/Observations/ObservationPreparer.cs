using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Observations;

/// <summary>
/// Prepares one section completion from a consistent store snapshot. It performs no I/O and owns no
/// clock: the caller supplies current eligibility time, while the observation retains its source time.
/// The store must compare the returned versions and commit every effect atomically.
/// </summary>
public static class ObservationPreparer
{
    public static PreparedSectionCommit Prepare(CommitContext context, SectionCompletion completion,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completion);
        if (context.IssuedCheck.Token != completion.Token || context.Section.Key != completion.Key)
            throw new ArgumentException("The context must identify this exact issued section completion.", nameof(context));

        // A newer unknown/error also supersedes older work. Comparing only the valid baseline would
        // let an older response replace newer health and potentially announce an obsolete opening.
        if (completion.Token.Ordinal <= context.Section.LatestCompletedOrdinal)
            return Prepared(new SectionEffects(EvaluationOutcome.IgnoredStale,
                context.Section.LatestOutcome!.Value, null, [], null));

        var evaluation = OpeningPolicy.Evaluate(context.Section.Baseline, completion.Observation);
        var resolution = RecipientResolver.Resolve(Members(context, completion.Key), completion.Observation,
            evaluation, evaluatedAtUtc);
        var ids = context.Subscriptions.ToDictionary(s => s.Id.ToString("N"), s => s.Id, StringComparer.Ordinal);
        var initializations = resolution.Initializations.Select(i =>
            new MemberInitializationEffect(ids[i.SubscriptionId], i.Generation));
        var health = evaluation.Outcome switch
        {
            EvaluationOutcome.IgnoredUnknown => completion.Outcome,
            EvaluationOutcome.RevalidationRequired => CheckOutcome.ParseChanged,
            _ => CheckOutcome.Success,
        };
        return Prepared(new SectionEffects(evaluation.Outcome, health,
            evaluation.Accepted ? evaluation.NextBaseline : null, initializations, resolution.Notification));

        PreparedSectionCommit Prepared(SectionEffects effects) => new(completion, context.Section.Version,
            context.CatalogRevision, effects, evaluatedAtUtc);
    }

    private static IEnumerable<WatchMember> Members(CommitContext context, SectionKey key) =>
        context.Subscriptions.Where(s => s.Status == WatchStatus.Active).SelectMany(s => s.Members
            .Where(m => m.Key == key)
            .Select(m => new WatchMember(s.Id.ToString("N"), key, s.ActivationGeneration,
                m.ActivationWatermark, s.InitialNoticeEnabled, m.InitializedByAttemptOrdinal is not null,
                SubscriptionStatus.Active, s.Expiry?.ExclusiveUtc)));
}
