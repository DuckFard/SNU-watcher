using System.Collections.Immutable;
using SnuWatcher.Application.Sources;
using Xunit;

namespace SnuWatcher.Application.Tests.Sources;

public sealed class SourceReadTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly CourseSearchData Search = new(new(2026, "opaqueTerm", "F32.102"), [], 1);
    private static readonly SourceProblem Problem = new(SourceProblemKind.RateLimited, "source_rate_limited", TimeSpan.FromDays(30));

    [Fact]
    public void CompletePartialAndFailedHaveUnambiguousDataAndProblemStates()
    {
        var complete = new SourceRead<CourseSearchData>(SourceReadStatus.Complete, Search, [], Received);
        var partial = new SourceRead<CourseSearchData>(SourceReadStatus.Partial, Search, [Problem], Received);
        var failed = new SourceRead<CourseSearchData>(SourceReadStatus.Failed, null, [Problem], Received);

        Assert.Same(Search, complete.Data);
        Assert.Empty(complete.Problems);
        Assert.Same(Search, partial.Data);
        Assert.Single(partial.Problems);
        Assert.Null(failed.Data);
        Assert.Equal(Received.ToUniversalTime(), complete.CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, complete.CompletedAtUtc.Offset);
        Assert.Equal(TimeSpan.FromDays(30), Problem.RetryAfter);
    }

    [Fact]
    public void UnsupportedResultStatesAndPartialMetadataCannotBeConstructed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceRead<CourseSearchData>((SourceReadStatus)99, Search, [], Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Complete, null, [], Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Complete, Search, [Problem], Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Partial, Search, [], Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Partial, null, [Problem], Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Failed, Search, [Problem], Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Failed, null, [], Received));
        var catalog = new CourseCatalogData(new(2026), [new(2026, "opaqueTerm", "2학기", null)], []);
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseCatalogData>(SourceReadStatus.Partial, catalog, [Problem], Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<object>(SourceReadStatus.Partial, new(), [Problem], Received));
    }

    [Fact]
    public void ProblemsAreBoundedImmutableSanitizedAndPreserveLongServerDelays()
    {
        var caller = new List<SourceProblem> { Problem };
        var result = new SourceRead<CourseSearchData>(SourceReadStatus.Partial, Search, caller, Received);
        caller.Clear();
        Assert.Same(Problem, Assert.Single(result.Problems));
        Assert.Throws<NotSupportedException>(() => ((IList<SourceProblem>)result.Problems)[0] = new(SourceProblemKind.Unavailable, "unavailable"));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Partial, Search,
            Enumerable.Repeat(Problem, SourceLimits.MaxProblems + 1), Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Partial, Search,
            default(ImmutableArray<SourceProblem>), Received));
        Assert.Throws<ArgumentException>(() => new SourceRead<CourseSearchData>(SourceReadStatus.Partial, Search, [null!], Received));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceProblem((SourceProblemKind)99, "unavailable"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceProblem(SourceProblemKind.RateLimited, "limited", TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() => new SourceProblem(SourceProblemKind.Unavailable, "private response body\n"));
        Assert.Throws<ArgumentException>(() => new SourceProblem(SourceProblemKind.Unavailable, new('x', 65)));
        Assert.Equal(TimeSpan.Zero, new SourceProblem(SourceProblemKind.RateLimited, "limited", TimeSpan.Zero).RetryAfter);
        Assert.Equal(TimeSpan.MaxValue, new SourceProblem(SourceProblemKind.RateLimited, "limited", TimeSpan.MaxValue).RetryAfter);
    }
}
