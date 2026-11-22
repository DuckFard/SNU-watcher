using System.Collections.Immutable;
using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Observations;

public sealed class SourceCompletionMapperTests
{
    private const string Selector = "U000200002U000300002";
    private static readonly DateTimeOffset Started = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Enrollment/Quota")]
    [InlineData("수강신청인원/정원(재학생)")]
    public void ValidRowRetainsReceiptTimeTokenAndCoreQuotaMeaning(string quotaLabel)
    {
        var issued = Issued();
        var receivedAt = Started.AddSeconds(2);
        var row = Row(quotaLabel: quotaLabel, observedAt: receivedAt, totalRegistered: 999);
        var read = Complete([row], completedAt: Started.AddSeconds(30));

        var completion = Assert.Single(SourceCompletionMapper.Map(issued, Query(), read));

        Assert.Same(issued.Token, completion.Token);
        Assert.Same(issued.Scope[0], completion.Key);
        Assert.Equal("012", completion.Key.Section);
        Assert.Equal("000", completion.Key.SubsubjectCode);
        Assert.Equal(issued.Token.Ordinal, completion.Observation.AttemptSequence);
        Assert.Equal(receivedAt, completion.ObservedAtUtc);
        Assert.Equal(CheckOutcome.Success, completion.Outcome);
        Assert.Equal(new SeatCount(119, 120), completion.Observation.Seats);
        Assert.Equal(1, completion.Observation.Seats!.Value.ApparentSeats);
        Assert.Equal(QuotaMeaning.EnrolledOverQuota, completion.Observation.Meaning);
        Assert.Null(completion.DiagnosticCode);
    }

    [Fact]
    public void ValidEarlyPageSurvivesLaterRateLimitAndMissingSectionIsOnlyIncomplete()
    {
        var first = Key();
        var second = Key("013");
        var issued = Issued([first, second]);
        var row = Row(key: first, observedAt: Started.AddSeconds(1));
        var read = Partial([row], [new(SourceProblemKind.RateLimited, "later_rate_limited", TimeSpan.FromMinutes(1))]);

        var completions = SourceCompletionMapper.Map(issued, Query(), read);

        Assert.Equal(2, completions.Length);
        Assert.Equal(CheckOutcome.Success, completions[0].Outcome);
        Assert.Equal(row.ObservedAtUtc, completions[0].ObservedAtUtc);
        Assert.Equal(CheckOutcome.Incomplete, completions[1].Outcome);
        Assert.Equal(ObservationKind.Unknown, completions[1].Observation.Kind);
        Assert.Equal("later_rate_limited", completions[1].DiagnosticCode);
        Assert.Equal(read.CompletedAtUtc, completions[1].ObservedAtUtc);
        Assert.Null(completions[1].Observation.Seats);
    }

    [Fact]
    public void CompleteMissingSectionIsNotFoundAtOutcomeTime()
    {
        var read = Complete([], completedAt: Started.AddSeconds(15));

        var completion = Assert.Single(SourceCompletionMapper.Map(Issued(), Query(), read));

        Assert.Equal(CheckOutcome.NotFound, completion.Outcome);
        Assert.Equal("section_not_found", completion.DiagnosticCode);
        Assert.Equal(ObservationKind.Unknown, completion.Observation.Kind);
        Assert.Equal(read.CompletedAtUtc, completion.ObservedAtUtc);
    }

    [Theory]
    [InlineData(SourceProblemKind.Unavailable, CheckOutcome.Unavailable)]
    [InlineData(SourceProblemKind.RateLimited, CheckOutcome.RateLimited)]
    [InlineData(SourceProblemKind.AccessRestricted, CheckOutcome.AccessRestricted)]
    [InlineData(SourceProblemKind.ParseChanged, CheckOutcome.ParseChanged)]
    [InlineData(SourceProblemKind.ResponseTooLarge, CheckOutcome.Unavailable)]
    [InlineData(SourceProblemKind.LimitReached, CheckOutcome.Incomplete)]
    [InlineData(SourceProblemKind.Maintenance, CheckOutcome.Unavailable)]
    [InlineData(SourceProblemKind.TermUnavailable, CheckOutcome.Unavailable)]
    public void FirstPageTypedFailureProducesUnknownForEveryIssuedKey(SourceProblemKind problem, CheckOutcome expected)
    {
        var issued = Issued([Key(), Key("013")]);
        var read = Failed(new(problem, "typed_source_problem"));

        var completions = SourceCompletionMapper.Map(issued, Query(), read);

        Assert.Equal(issued.Scope, completions.Select(c => c.Key));
        Assert.All(completions, completion =>
        {
            Assert.Same(issued.Token, completion.Token);
            Assert.Equal(expected, completion.Outcome);
            Assert.Equal(ObservationKind.Unknown, completion.Observation.Kind);
            Assert.Null(completion.Observation.Seats);
            Assert.Equal(read.CompletedAtUtc, completion.ObservedAtUtc);
            Assert.Equal(problem == SourceProblemKind.Maintenance ? "source_maintenance" : "typed_source_problem", completion.DiagnosticCode);
        });
    }

    [Theory]
    [InlineData(SourceReadStatus.Partial)]
    [InlineData(SourceReadStatus.Failed)]
    public void MultipleProblemsUseDeterministicFirstCodeWithoutPoisoningGoodRows(SourceReadStatus status)
    {
        SourceProblem[] problems = [new(SourceProblemKind.AccessRestricted, "first_problem"), new(SourceProblemKind.RateLimited, "second_problem")];
        var read = status == SourceReadStatus.Partial ? Partial([], problems)
            : new SourceRead<CourseSearchData>(status, null, problems, Started.AddSeconds(20));

        var completion = Assert.Single(SourceCompletionMapper.Map(Issued(), Query(), read));

        Assert.Equal(status == SourceReadStatus.Partial ? CheckOutcome.Incomplete : CheckOutcome.AccessRestricted, completion.Outcome);
        Assert.Equal("first_problem", completion.DiagnosticCode);
    }

    [Theory]
    [InlineData(null, 120, "Enrollment/Quota", "invalid_counts", ObservationKind.Unknown)]
    [InlineData(119, null, "Enrollment/Quota", "invalid_counts", ObservationKind.Unknown)]
    [InlineData(119, 120, null, "quota_unverified", ObservationKind.SemanticsUnverified)]
    [InlineData(119, 120, "Total/Limit", "quota_unverified", ObservationKind.SemanticsUnverified)]
    public void InvalidCountsOrQuotaCannotReplaceBaselineInitializeOrNotify(int? registered, int? capacity,
        string? quotaLabel, string code, ObservationKind kind)
    {
        var issued = Issued();
        var row = Row(registered: registered, capacity: capacity, quotaLabel: quotaLabel);
        var completion = Assert.Single(SourceCompletionMapper.Map(issued, Query(), Complete([row])));
        var baseline = new SeatBaseline(Key(), new SeatCount(120, 120), QuotaMeaning.EnrolledOverQuota, 1, Started.AddMinutes(-1));
        var state = new SectionSnapshot(Key(), 1, 1, CheckOutcome.Success, baseline.ObservedAt, baseline.ObservedAt, baseline);
        var watch = ObservationFixture.Watch(key: Key(), watermark: 1);

        var prepared = ObservationPreparer.Prepare(new(issued, state, 1, [watch]), completion, Started.AddMinutes(1));

        Assert.Equal(CheckOutcome.ParseChanged, completion.Outcome);
        Assert.Equal(kind, completion.Observation.Kind);
        Assert.Equal(code, completion.DiagnosticCode);
        Assert.Equal(row.ObservedAtUtc, completion.ObservedAtUtc);
        Assert.Equal(CheckOutcome.ParseChanged, prepared.Effects.HealthOutcome);
        Assert.Null(prepared.Effects.NextBaseline);
        Assert.Empty(prepared.Effects.Initializations);
        Assert.Null(prepared.Effects.Notification);
        var applied = ObservationFixture.Apply(state, [watch], prepared);
        Assert.Same(baseline, applied.Section.Baseline);
        Assert.Equal(baseline.ObservedAt, applied.Section.LastSuccessfulAtUtc);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(125, 120)]
    public void ZeroQuotaAndOvercapacityRemainKnownValidCounts(int registered, int capacity)
    {
        var completion = Assert.Single(SourceCompletionMapper.Map(Issued(), Query(),
            Complete([Row(registered: registered, capacity: capacity)])));

        Assert.Equal(CheckOutcome.Success, completion.Outcome);
        Assert.Equal(ObservationKind.Valid, completion.Observation.Kind);
        Assert.Equal(0, completion.Observation.Seats!.Value.ApparentSeats);
    }

    [Theory]
    [InlineData("year")]
    [InlineData("season")]
    [InlineData("term_parts")]
    [InlineData("course")]
    [InlineData("case")]
    [InlineData("section")]
    [InlineData("subsubject")]
    public void UnrelatedRowsNeverAliasAFullScopedKey(string mismatch)
    {
        var other = mismatch switch
        {
            "year" => Key(year: 2025),
            "season" => Key(detail: "U000300003"),
            "term_parts" => Key(semester: "U0002", detail: "00002U000300002"),
            "course" => Key(course: "F33.107"),
            "case" => Key(course: "f32.102"),
            "section" => Key("102"),
            _ => Key(subsubject: "001"),
        };

        var completion = Assert.Single(SourceCompletionMapper.Map(Issued(), Query(), Complete([Row(key: other)])));

        Assert.Equal(Key(), completion.Key);
        Assert.Equal(CheckOutcome.NotFound, completion.Outcome);
        Assert.Null(completion.Observation.Seats);
    }

    [Fact]
    public void InputMutationAndRowsAddedDuringRequestCannotExpandIssuedScopeOrChangeMappedFacts()
    {
        List<SectionKey> scope = [Key("013"), Key()];
        var issued = Issued(scope);
        List<CourseSearchRow> rows = [Row(), Row(key: Key("013"), registered: 118), Row(key: Key("014"))];
        var read = Complete(rows);
        scope.Add(Key("014"));
        rows.Clear();

        var completions = SourceCompletionMapper.Map(issued, Query(), read);

        Assert.Equal(new[] { "013", "012" }, completions.Select(c => c.Key.Section));
        Assert.Equal(new[] { 2, 1 }, completions.Select(c => c.Observation.Seats!.Value.ApparentSeats));
        Assert.Throws<NotSupportedException>(() => ((IList<SectionCompletion>)completions).Clear());
        Assert.Equal(2, completions.Length);
        Assert.Equal(3, read.Data!.Rows.Length);
    }

    [Theory]
    [InlineData("year")]
    [InlineData("term")]
    [InlineData("course")]
    [InlineData("name")]
    [InlineData("instructor")]
    [InlineData("up_department")]
    [InlineData("department")]
    public void ReturnedQueryMustMatchAllRequestedFields(string mismatch)
    {
        var actual = mismatch switch
        {
            "year" => Query(year: 2025),
            "term" => Query(selector: "U000200002U000300003"),
            "course" => Query(course: "F33.107"),
            "name" => Query(name: "Statistics"),
            "instructor" => Query(instructor: "Professor"),
            "up_department" => Query(upDepartment: "001"),
            _ => Query(department: "002"),
        };
        var read = Complete([], query: actual);

        Assert.Throws<ArgumentException>(() => SourceCompletionMapper.Map(Issued(), Query(), read));
    }

    [Theory]
    [InlineData("no_course")]
    [InlineData("name")]
    [InlineData("instructor")]
    [InlineData("up_department")]
    [InlineData("department")]
    public void WatchQueryMustBeExactCourseWithoutDisplayFiltersEvenForFailedRead(string extra)
    {
        var query = extra switch
        {
            "no_course" => Query(course: null, name: "Statistics"),
            "name" => Query(name: "Statistics"),
            "instructor" => Query(instructor: "Professor"),
            "up_department" => Query(upDepartment: "001"),
            _ => Query(department: "002"),
        };

        Assert.Throws<ArgumentException>(() => SourceCompletionMapper.Map(Issued(), query,
            Failed(new(SourceProblemKind.Unavailable, "source_unavailable"))));
    }

    [Theory]
    [InlineData("year")]
    [InlineData("term")]
    [InlineData("course")]
    [InlineData("mixed_course")]
    public void IssuedScopeMustBelongToRequestedExactYearTermAndCourse(string mismatch)
    {
        var scope = mismatch switch
        {
            "year" => new[] { Key(year: 2025) },
            "term" => [Key(detail: "U000300003")],
            "course" => [Key(course: "F33.107")],
            _ => [Key(), Key("013", course: "F33.107")],
        };

        Assert.Throws<ArgumentException>(() => SourceCompletionMapper.Map(Issued(scope), Query(), Complete([])));
    }

    [Fact]
    public void EquivalentValueQueryAndSameInstantOffsetAreAccepted()
    {
        var query = Query();
        var equivalent = new CourseSearchQuery(2026, Selector, "F32.102", " ", "", null, " ");
        var receivedAt = Started.AddSeconds(3).ToOffset(TimeSpan.FromHours(9));

        var completion = Assert.Single(SourceCompletionMapper.Map(Issued(), query,
            Complete([Row(observedAt: receivedAt)], query: equivalent)));

        Assert.Equal(CheckOutcome.Success, completion.Outcome);
        Assert.Equal(receivedAt.ToUniversalTime(), completion.ObservedAtUtc);
    }

    [Fact]
    public void CachedPreAttemptRowCannotInitializeLaterActivationAndIsNeverRestamped()
    {
        var issued = Issued();
        var sourceAt = Started.AddMinutes(-5);
        var completion = Assert.Single(SourceCompletionMapper.Map(issued, Query(),
            Complete([Row(observedAt: sourceAt)], completedAt: Started.AddSeconds(2))));
        var watch = ObservationFixture.Watch(key: Key(), watermark: 1);

        var prepared = ObservationPreparer.Prepare(new(issued, SectionSnapshot.Unchecked(Key()), 1, [watch]),
            completion, Started.AddSeconds(3));

        Assert.Equal(CheckOutcome.Incomplete, completion.Outcome);
        Assert.Equal("observation_before_attempt", completion.DiagnosticCode);
        Assert.Equal(sourceAt, completion.ObservedAtUtc);
        Assert.Equal(ObservationKind.Unknown, completion.Observation.Kind);
        Assert.Same(issued.Token, completion.Token);
        Assert.Empty(prepared.Effects.Initializations);
        Assert.Null(prepared.Effects.Notification);
        Assert.Null(prepared.Effects.NextBaseline);
    }

    [Fact]
    public void ReceiptExactlyAtIssuanceIsAcceptedWithoutInventingProvenance()
    {
        var completion = Assert.Single(SourceCompletionMapper.Map(Issued(), Query(), Complete([Row(observedAt: Started)])));

        Assert.Equal(CheckOutcome.Success, completion.Outcome);
        Assert.Equal(Started, completion.ObservedAtUtc);
    }

    [Fact]
    public void FinalOutcomeClockRollbackDoesNotRestampValidEarlierPageOrUnknownFailure()
    {
        var rowAt = Started.AddSeconds(10);
        var success = Assert.Single(SourceCompletionMapper.Map(Issued(), Query(),
            Complete([Row(observedAt: rowAt)], completedAt: Started.AddSeconds(5))));
        var failureAt = Started.AddMinutes(-1);
        var failure = Assert.Single(SourceCompletionMapper.Map(Issued(), Query(),
            Failed(new(SourceProblemKind.Unavailable, "source_unavailable"), failureAt)));

        Assert.Equal(CheckOutcome.Success, success.Outcome);
        Assert.Equal(rowAt, success.ObservedAtUtc);
        Assert.Equal(CheckOutcome.Unavailable, failure.Outcome);
        Assert.Equal(failureAt, failure.ObservedAtUtc);
    }

    [Fact]
    public void NullArgumentsAreRejectedAtTheBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => SourceCompletionMapper.Map(null!, Query(), Complete([])));
        Assert.Throws<ArgumentNullException>(() => SourceCompletionMapper.Map(Issued(), null!, Complete([])));
        Assert.Throws<ArgumentNullException>(() => SourceCompletionMapper.Map(Issued(), Query(), null!));
    }

    private static SectionKey Key(string section = "012", int year = 2026, string semester = "U000200002",
        string detail = "U000300002", string course = "F32.102", string subsubject = "000") =>
        new(year, semester, detail, course, section, subsubject);

    private static IssuedCheck Issued(IEnumerable<SectionKey>? scope = null) =>
        new(new CheckAttemptToken(Guid.NewGuid(), 2), Started, scope ?? [Key()]);

    private static CourseSearchQuery Query(int year = 2026, string selector = Selector, string? course = "F32.102",
        string? name = null, string? instructor = null, string? upDepartment = null, string? department = null) =>
        new(year, selector, course, name, instructor, upDepartment, department);

    private static CourseSearchRow Row(SectionKey? key = null, int? registered = 119, int? capacity = 120,
        int? totalRegistered = 119, string? quotaLabel = "Enrollment/Quota", DateTimeOffset? observedAt = null) =>
        new(key ?? Key(), "Statistics", "Professor", "Statistics Department", "Mon 14:00", registered,
            capacity, totalRegistered, quotaLabel, observedAt ?? Started.AddSeconds(1));

    private static SourceRead<CourseSearchData> Complete(IEnumerable<CourseSearchRow> rows,
        CourseSearchQuery? query = null, DateTimeOffset? completedAt = null) =>
        new(SourceReadStatus.Complete, new CourseSearchData(query ?? Query(), rows, 1), [], completedAt ?? Started.AddSeconds(20));

    private static SourceRead<CourseSearchData> Partial(IEnumerable<CourseSearchRow> rows, IEnumerable<SourceProblem> problems) =>
        new(SourceReadStatus.Partial, new CourseSearchData(Query(), rows, 1), problems, Started.AddSeconds(20));

    private static SourceRead<CourseSearchData> Failed(SourceProblem problem, DateTimeOffset? completedAt = null) =>
        new(SourceReadStatus.Failed, null, [problem], completedAt ?? Started.AddSeconds(20));
}
