namespace SnuWatcher.Core.Tests;

internal static class Fixture
{
    public static readonly DateTimeOffset Start = new(2026, 10, 31, 9, 0, 0, TimeSpan.FromHours(9));

    public static SectionKey Key(string course = "F32.102", string section = "012") =>
        new(2026, "U000200002", "U000300002", course, section, "0");

    public static DateTimeOffset At(long sequence) => Start.AddSeconds(sequence * 30);

    public static SeatObservation Valid(SectionKey key, long sequence, int registered, int capacity) =>
        SeatObservation.Valid(key, sequence, At(sequence), new SeatCount(registered, capacity), QuotaMeaning.EnrolledOverQuota);

    /// <summary>Feeds counts through the policy in order, threading the baseline like the app will.</summary>
    public static List<OpeningEvaluation> Run(SectionKey key, params (int Registered, int Capacity)[] counts)
    {
        var results = new List<OpeningEvaluation>();
        SeatBaseline? baseline = null;
        for (var i = 0; i < counts.Length; i++)
        {
            var evaluation = OpeningPolicy.Evaluate(baseline, Valid(key, i + 1, counts[i].Registered, counts[i].Capacity));
            baseline = evaluation.NextBaseline;
            results.Add(evaluation);
        }
        return results;
    }

    public static WatchMember Member(string id, SectionKey key, long activation = 0, bool initial = true,
        bool initialized = false, SubscriptionStatus status = SubscriptionStatus.Active,
        DateTimeOffset? expires = null, long generation = 1) =>
        new(id, key, generation, activation, initial, initialized, status, expires);
}
