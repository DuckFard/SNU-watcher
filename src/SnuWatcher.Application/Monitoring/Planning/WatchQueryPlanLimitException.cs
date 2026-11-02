namespace SnuWatcher.Application.Monitoring.Planning;

/// <summary>
/// The active section union exceeds this planning operation's resource boundary. This is independent
/// of persistence command limits and must never be handled by silently dropping watched sections.
/// </summary>
public sealed class WatchQueryPlanLimitException : ArgumentException
{
    internal WatchQueryPlanLimitException(int maxUniqueSections)
        : base("Active watch sections exceed the planning resource bound.", "maxUniqueSections") =>
        MaxUniqueSections = maxUniqueSections;

    public string Code => "watch_plan_limit_exceeded";
    public int MaxUniqueSections { get; }
}
