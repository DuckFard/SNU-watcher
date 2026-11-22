using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Snu;
using SnuWatcher.Infrastructure.Snu.Gateway;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuGatewayMappingTests
{
    private static readonly DateTimeOffset PageAt = new(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly DateTimeOffset DoneAt = PageAt.AddMinutes(2);
    private static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "012", "00");
    private static readonly SourceTerm Term = new(2026, "U000200002U000300002", "2학기", "Fall");
    private static readonly CourseSearchQuery Query = new(2026, Term.Selector, "F32.102");
    private static readonly SourceSearchQuery Dispatched = new(Term, CourseCode: "F32.102");

    [Fact]
    public void IdentityRoundTripPreservesAllSixFieldsAndLeadingZeros()
    {
        var raw = SnuIdentityMapper.ToSourceIdentity(Key);
        Assert.Equal(2026, raw.Year);
        Assert.Equal("U000200002", raw.SemesterCode);
        Assert.Equal("U000300002", raw.DetailSemesterCode);
        Assert.Equal("F32.102", raw.CourseCode);
        Assert.Equal("012", raw.Section);
        Assert.Equal("00", raw.SubsubjectCode);
        Assert.Equal(Key, SnuIdentityMapper.ToSectionKey(raw));
        Assert.Throws<ArgumentNullException>(() => SnuIdentityMapper.ToSectionKey(null!));
        Assert.Throws<ArgumentNullException>(() => SnuIdentityMapper.ToSourceIdentity(null!));
    }

    [Fact]
    public void SearchRequestRequiresExactCatalogReportedTermAndRetainsEveryNormalizedFilter()
    {
        var query = new CourseSearchQuery(2026, Term.Selector, "F32.102", " 통계학 ", "Kim", "001", "002");
        var catalog = Catalog().Data!;
        Assert.True(SnuIdentityMapper.TryCreateSearchQuery(query, catalog, out var request));
        Assert.NotNull(request);
        Assert.Equal(Term, request.Term);
        Assert.Equal(query.CourseCode, request.CourseCode);
        Assert.Equal(query.Name, request.Name);
        Assert.Equal(query.Instructor, request.Instructor);
        Assert.Equal(query.UpDepartment, request.UpDepartment);
        Assert.Equal(query.Department, request.Department);
        Assert.Null(request.Major);

        Assert.False(SnuIdentityMapper.TryCreateSearchQuery(new(2026, "U000200002", "F32.102"), catalog, out var absent));
        Assert.Null(absent);
        Assert.False(SnuIdentityMapper.TryCreateSearchQuery(new(2025, Term.Selector, "F32.102"), catalog, out _));
    }

    [Fact]
    public void CatalogMapsSourceLabelsAndSnapshotsRawListsWithoutInferringAliases()
    {
        var terms = new List<SourceTerm> { Term };
        var departments = new List<SourceDepartment> { new("001", "통계학과\n학부", "Statistics", "UP_DEPT") };
        var read = SnuDataMapper.Catalog(new(2026, Term.Selector, "001"), new(2026, terms, departments), DoneAt);
        terms.Clear();
        departments.Clear();

        Assert.Equal(SourceReadStatus.Complete, read.Status);
        Assert.Equal(DoneAt.ToUniversalTime(), read.CompletedAtUtc);
        Assert.Equal(Term.Selector, Assert.Single(read.Data!.Terms).Selector);
        Assert.Equal("2학기", read.Data.Terms[0].Name);
        Assert.Equal("Fall", read.Data.Terms[0].EnglishName);
        Assert.Equal("통계학과\n학부", Assert.Single(read.Data.Departments).Name);
        Assert.Equal("001", read.Data.Query.UpDepartment);
        Assert.Empty(read.Problems);
    }

    [Theory]
    [InlineData("year")]
    [InlineData("empty_terms")]
    [InlineData("null_term")]
    [InlineData("null_departments")]
    [InlineData("oversized_label")]
    public void InvalidCatalogShapeFailsWithoutFabricatingMetadata(string mode)
    {
        var raw = mode switch
        {
            "year" => new SourceCatalog(2025, [Term], []),
            "empty_terms" => new SourceCatalog(2026, [], []),
            "null_term" => new SourceCatalog(2026, [null!], []),
            "null_departments" => new SourceCatalog(2026, [Term], null!),
            _ => new SourceCatalog(2026, [Term with { Name = new('x', SourceLimits.MaxShortDisplay + 1) }], []),
        };
        AssertParseFailure(SnuDataMapper.Catalog(new(2026), raw, DoneAt));
    }

    [Fact]
    public void UnreportedCatalogTermIsUnavailableRatherThanAnInferredSeasonalAlias()
    {
        var read = SnuDataMapper.Catalog(new(2026, "U000200002"), new(2026, [Term], []), DoneAt);
        Assert.Equal(SourceReadStatus.Failed, read.Status);
        Assert.Null(read.Data);
        Assert.Equal(SourceProblemKind.TermUnavailable, Assert.Single(read.Problems).Kind);
        Assert.Equal("term_unconfirmed", read.Problems[0].DiagnosticCode);
    }

    [Theory]
    [InlineData(SourceFailureKind.Unavailable, SourceProblemKind.Unavailable, "source_unavailable")]
    [InlineData(SourceFailureKind.AccessRestricted, SourceProblemKind.AccessRestricted, "source_access_restricted")]
    [InlineData(SourceFailureKind.RateLimited, SourceProblemKind.RateLimited, "source_rate_limited")]
    [InlineData(SourceFailureKind.ParseChanged, SourceProblemKind.ParseChanged, "source_parse_changed")]
    [InlineData(SourceFailureKind.ResponseTooLarge, SourceProblemKind.ResponseTooLarge, "source_response_too_large")]
    [InlineData(SourceFailureKind.LimitReached, SourceProblemKind.LimitReached, "source_limit_reached")]
    [InlineData(SourceFailureKind.Maintenance, SourceProblemKind.Maintenance, "source_maintenance")]
    public void FailuresUseFixedCodesNeverRawMessagesAndKeepLongServerDelay(SourceFailureKind rawKind, SourceProblemKind kind, string code)
    {
        const string privateMessage = "Cookie: private-session; Authorization: secret\n<html>server body</html>";
        var delay = TimeSpan.FromDays(90);
        var problem = SourceFailureMapper.Map(new SourceFailure(rawKind, privateMessage, delay));
        var exception = SourceFailureMapper.Map(new SnuSourceException(rawKind, privateMessage, delay));
        Assert.Equal(kind, problem.Kind);
        Assert.Equal(code, problem.DiagnosticCode);
        Assert.Equal(delay, problem.RetryAfter);
        Assert.Equal(problem, exception);
        Assert.DoesNotContain("private", problem.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFailureAndNegativeDelayAreSafeAndDoNotEscapeAsInvalidContracts()
    {
        var problem = SourceFailureMapper.Map(new SourceFailure((SourceFailureKind)999, "private response", TimeSpan.FromSeconds(-1)));
        Assert.Equal(SourceProblemKind.ParseChanged, problem.Kind);
        Assert.Equal("source_parse_changed", problem.DiagnosticCode);
        Assert.Equal(TimeSpan.Zero, problem.RetryAfter);
    }

    [Fact]
    public void CompleteSearchPreservesOriginalRowTimeCountsQuotaAndImmutableSnapshot()
    {
        var rows = new List<SourceSection> { Row() };
        var read = SnuDataMapper.Search(Query, Dispatched, Result(rows), DoneAt.AddMinutes(1));
        rows.Clear();
        Assert.Equal(SourceReadStatus.Complete, read.Status);
        Assert.Equal(DoneAt.ToUniversalTime(), read.CompletedAtUtc);
        var row = Assert.Single(read.Data!.Rows);
        Assert.Equal(Key, row.Key);
        Assert.Equal(116, row.Registered);
        Assert.Equal(120, row.Capacity);
        Assert.Equal(999, row.TotalRegistered);
        Assert.Equal("Enrollment/Quota", row.QuotaLabel);
        Assert.Equal(PageAt.ToUniversalTime(), row.ObservedAtUtc);
        Assert.Equal("월\t14:00\n수 14:00", row.Schedule);
        Assert.Equal(Query, read.Data.Query);
    }

    [Fact]
    public void InvalidCountsBecomeNullOnTheirOwnRowWithoutPoisoningVerifiedSearchCoverage()
    {
        var raw = Row() with { Registered = -1, Capacity = 100_001, TotalRegistered = -5 };
        var read = SnuDataMapper.Search(Query, Dispatched, Result([raw]), DoneAt);
        Assert.Equal(SourceReadStatus.Complete, read.Status);
        var row = Assert.Single(read.Data!.Rows);
        Assert.Null(row.Registered);
        Assert.Null(row.Capacity);
        Assert.Null(row.TotalRegistered);
        var issued = new IssuedCheck(new(Guid.NewGuid(), 1), PageAt.AddSeconds(-1), [Key]);
        Assert.Equal(CheckOutcome.ParseChanged, Assert.Single(SourceCompletionMapper.Map(issued, Query, read)).Outcome);
    }

    [Fact]
    public void ZeroQuotaAndOverCapacityRemainKnownCountsAndDetailsDoNotSupplyAReplacementQuota()
    {
        var read = SnuDataMapper.Search(Query, Dispatched, Result([Row() with { Registered = 100, Capacity = 0, TotalRegistered = 0 }]), DoneAt);
        var row = Assert.Single(read.Data!.Rows);
        Assert.Equal(100, row.Registered);
        Assert.Equal(0, row.Capacity);
        Assert.Equal(0, row.TotalRegistered);
        var issued = new IssuedCheck(new(Guid.NewGuid(), 1), PageAt.AddSeconds(-1), [Key]);
        Assert.Equal(CheckOutcome.Success, Assert.Single(SourceCompletionMapper.Map(issued, Query, read)).Outcome);
        var details = SnuDataMapper.Details(Key, Details([]) with { Capacity = -1 }, DoneAt);
        Assert.Equal(SourceReadStatus.Complete, details.Status);
        Assert.Null(details.Data!.Capacity);
        Assert.Equal(0, row.Capacity);
    }

    [Fact]
    public void TotalEnrollmentAndUnverifiedQuotaCannotReplaceCurrentRegisteredOverQuotaMeaning()
    {
        var raw = Row() with { Registered = 120, Capacity = 120, TotalRegistered = 1, CountLabel = "unknown quota meaning" };
        var read = SnuDataMapper.Search(Query, Dispatched, Result([raw]), DoneAt);
        var issued = new IssuedCheck(new(Guid.NewGuid(), 1), PageAt.AddSeconds(-1), [Key]);
        var completion = Assert.Single(SourceCompletionMapper.Map(issued, Query, read));
        Assert.Equal(CheckOutcome.ParseChanged, completion.Outcome);
        Assert.Equal(ObservationKind.SemanticsUnverified, completion.Observation.Kind);
        Assert.Equal(120, Assert.Single(read.Data!.Rows).Registered);
    }

    [Fact]
    public void LaterRateLimitPreservesVerifiedRowsTheirTimesAndOriginalDelay()
    {
        var raw = new SourceSearchResult([Row()], false, [new(SourceFailureKind.RateLimited, "private body", TimeSpan.FromHours(3))], 1)
            { CompletedAtUtc = DoneAt };
        var read = SnuDataMapper.Search(Query, Dispatched, raw, DoneAt.AddHours(1));
        Assert.Equal(SourceReadStatus.Partial, read.Status);
        Assert.Equal(PageAt.ToUniversalTime(), Assert.Single(read.Data!.Rows).ObservedAtUtc);
        Assert.Equal(SourceProblemKind.RateLimited, Assert.Single(read.Problems).Kind);
        Assert.Equal(TimeSpan.FromHours(3), read.Problems[0].RetryAfter);
        var missing = new SectionKey(Key.Year, Key.SemesterCode, Key.DetailSemesterCode, Key.CourseCode, "013", Key.SubsubjectCode);
        var issued = new IssuedCheck(new(Guid.NewGuid(), 1), PageAt.AddSeconds(-1), [Key, missing]);
        var completions = SourceCompletionMapper.Map(issued, Query, read);
        Assert.Equal(CheckOutcome.Success, completions.Single(item => item.Key == Key).Outcome);
        Assert.Equal(CheckOutcome.Incomplete, completions.Single(item => item.Key == missing).Outcome);
    }

    [Theory]
    [InlineData("unstamped")]
    [InlineData("identity")]
    [InlineData("other_term")]
    [InlineData("display")]
    [InlineData("null_row")]
    public void InvalidRowIsSkippedWhileOtherVerifiedRowsSurviveAndMissingCoverageStaysPartial(string mode)
    {
        var valid = Row();
        var invalid = mode switch
        {
            "unstamped" => Row("013") with { ObservedAtUtc = null },
            "identity" => Row("013") with { Identity = null! },
            "other_term" => Row("013") with { Identity = new(2026, Key.SemesterCode, "U000300001", Key.CourseCode, "013", "00") },
            "display" => Row("013") with { Name = "private\0invalid" },
            _ => null!,
        };
        var read = SnuDataMapper.Search(Query, Dispatched, Result([valid, invalid]), DoneAt);
        Assert.Equal(SourceReadStatus.Partial, read.Status);
        Assert.Equal(Key, Assert.Single(read.Data!.Rows).Key);
        Assert.Contains(read.Problems, problem => problem.Kind == SourceProblemKind.ParseChanged);
        Assert.Equal(PageAt.ToUniversalTime(), read.Data.Rows[0].ObservedAtUtc);
    }

    [Fact]
    public void AllInvalidRowsRemainPartialAndNeverBecomeVerifiedEmptyNotFound()
    {
        var read = SnuDataMapper.Search(Query, Dispatched, Result([Row() with { ObservedAtUtc = null }]), DoneAt);
        Assert.Equal(SourceReadStatus.Partial, read.Status);
        Assert.Empty(read.Data!.Rows);
        Assert.Equal(1, read.Data.PagesFetched);
        var issued = new IssuedCheck(new(Guid.NewGuid(), 1), PageAt.AddSeconds(-1), [Key]);
        Assert.Equal(CheckOutcome.Incomplete, Assert.Single(SourceCompletionMapper.Map(issued, Query, read)).Outcome);
    }

    [Fact]
    public void ContradictoryDuplicateRowsAreExcludedInsteadOfChoosingConvenientCounts()
    {
        var read = SnuDataMapper.Search(Query, Dispatched, Result([Row(), Row() with { Registered = 119 }]), DoneAt);
        Assert.Equal(SourceReadStatus.Partial, read.Status);
        Assert.Empty(read.Data!.Rows);
        Assert.Contains(read.Problems, problem => problem.Kind == SourceProblemKind.ParseChanged);
    }

    [Theory]
    [InlineData("year")]
    [InlineData("term")]
    [InlineData("course")]
    [InlineData("name")]
    [InlineData("instructor")]
    [InlineData("up_department")]
    [InlineData("department")]
    [InlineData("major")]
    public void DispatchedSearchMustRoundTripEveryQueryFieldExactly(string changed)
    {
        var request = changed switch
        {
            "year" => Dispatched with { Term = Term with { Year = 2025 } },
            "term" => Dispatched with { Term = Term with { Selector = "U000200002" } },
            "course" => Dispatched with { CourseCode = "F32.103" },
            "name" => Dispatched with { Name = "statistics" },
            "instructor" => Dispatched with { Instructor = "Kim" },
            "up_department" => Dispatched with { UpDepartment = "001" },
            "department" => Dispatched with { Department = "002" },
            _ => Dispatched with { Major = "003" },
        };
        AssertParseFailure(SnuDataMapper.Search(Query, request, Result([Row()]), DoneAt));
    }

    [Fact]
    public void MissingQueryReceiptFailsRatherThanStampingOldCountsWithCurrentTime()
    {
        var read = SnuDataMapper.Search(Query, Dispatched, Result([Row()]) with { CompletedAtUtc = null }, DoneAt);
        AssertParseFailure(read);
        Assert.Equal(DoneAt.ToUniversalTime(), read.CompletedAtUtc);
    }

    [Fact]
    public void CompleteEmptyAndFailureBeforeVerifiedPagesRemainDistinct()
    {
        var empty = SnuDataMapper.Search(Query, Dispatched, Result([]), DoneAt);
        Assert.Equal(SourceReadStatus.Complete, empty.Status);
        Assert.Empty(empty.Data!.Rows);
        var failed = SnuDataMapper.Search(Query, Dispatched,
            new([], false, [new(SourceFailureKind.RateLimited, "private", TimeSpan.FromMinutes(2))], 0) { CompletedAtUtc = DoneAt }, DoneAt);
        Assert.Equal(SourceReadStatus.Failed, failed.Status);
        Assert.Null(failed.Data);
        Assert.Equal(SourceProblemKind.RateLimited, Assert.Single(failed.Problems).Kind);
    }

    [Fact]
    public void InconsistentIncompleteFlagAndMalformedFailureEntryPreserveRowsAsPartial()
    {
        var missingProblem = SnuDataMapper.Search(Query, Dispatched, Result([Row()]) with { IsComplete = false }, DoneAt);
        Assert.Equal(SourceReadStatus.Partial, missingProblem.Status);
        Assert.Single(missingProblem.Data!.Rows);
        Assert.Equal(SourceProblemKind.ParseChanged, Assert.Single(missingProblem.Problems).Kind);
        var invalidFailure = SnuDataMapper.Search(Query, Dispatched, Result([Row()]) with { Failures = [null!] }, DoneAt);
        Assert.Equal(SourceReadStatus.Partial, invalidFailure.Status);
        Assert.Single(invalidFailure.Data!.Rows);
        Assert.Equal(SourceProblemKind.ParseChanged, Assert.Single(invalidFailure.Problems).Kind);
    }

    [Fact]
    public void RepeatedFailuresRetainTheLongestServerDelayWithoutDuplicatingProblemEntries()
    {
        var raw = Result([Row()]) with
        {
            IsComplete = false,
            Failures = [new(SourceFailureKind.RateLimited, "privateA", TimeSpan.FromHours(1)),
                new(SourceFailureKind.RateLimited, "privateB", TimeSpan.FromDays(3))],
        };
        var read = SnuDataMapper.Search(Query, Dispatched, raw, DoneAt);
        Assert.Equal(SourceReadStatus.Partial, read.Status);
        Assert.Equal(TimeSpan.FromDays(3), Assert.Single(read.Problems).RetryAfter);
        Assert.Single(read.Data!.Rows);
    }

    [Theory]
    [InlineData("null_rows")]
    [InlineData("null_failures")]
    [InlineData("rows_limit")]
    [InlineData("problems_limit")]
    [InlineData("negative_pages")]
    [InlineData("pages_limit")]
    public void UnsupportedQueryEnvelopeFailsWithinBoundsInsteadOfPublishingUnverifiedCoverage(string mode)
    {
        var raw = mode switch
        {
            "null_rows" => Result(null!),
            "null_failures" => Result([Row()]) with { Failures = null! },
            "rows_limit" => Result(Enumerable.Repeat(Row(), SourceLimits.MaxRows + 1).ToArray()),
            "problems_limit" => Result([]) with { Failures = Enumerable.Repeat(new SourceFailure(SourceFailureKind.Unavailable, "private"), SourceLimits.MaxProblems + 1).ToArray() },
            "negative_pages" => Result([]) with { PagesFetched = -1 },
            _ => Result([]) with { PagesFetched = SourceLimits.MaxPages + 1 },
        };
        AssertParseFailure(SnuDataMapper.Search(Query, Dispatched, raw, DoneAt));
    }

    [Fact]
    public void DetailsPreserveExactIdentityMetadataAndMeetingSnapshot()
    {
        var meetings = new List<SourceMeeting> { new("월\t14:00\n수 14:00", "강의", "001\n012") };
        var raw = Details(meetings);
        var read = SnuDataMapper.Details(Key, raw, DoneAt);
        meetings.Clear();
        Assert.Equal(SourceReadStatus.Complete, read.Status);
        Assert.Equal(Key, read.Data!.Key);
        Assert.Equal("Statistics", read.Data.EnglishName);
        Assert.Equal("Kim", read.Data.EnglishInstructor);
        Assert.Equal("Korean", read.Data.InstructionLanguage);
        Assert.Equal("제한\n설명", read.Data.Restrictions);
        Assert.Equal("001\n012", Assert.Single(read.Data.Meetings).Room);
        Assert.Equal(3m, read.Data.Credits);
    }

    [Theory]
    [InlineData("section")]
    [InlineData("subsubject")]
    [InlineData("detail_semester")]
    [InlineData("meetings")]
    [InlineData("name")]
    public void DetailMismatchOrInvalidMetadataFailsInsteadOfSalvagingByName(string mode)
    {
        var raw = Details([]);
        raw = mode switch
        {
            "section" => raw with { Identity = new(2026, Key.SemesterCode, Key.DetailSemesterCode, Key.CourseCode, "013", "00") },
            "subsubject" => raw with { Identity = new(2026, Key.SemesterCode, Key.DetailSemesterCode, Key.CourseCode, Key.Section, "01") },
            "detail_semester" => raw with { Identity = new(2026, Key.SemesterCode, "U000300001", Key.CourseCode, Key.Section, "00") },
            "meetings" => raw with { Meetings = null! },
            _ => raw with { Name = "\n" },
        };
        AssertParseFailure(SnuDataMapper.Details(Key, raw, DoneAt));
    }

    private static SourceRead<CourseCatalogData> Catalog() =>
        SnuDataMapper.Catalog(new(2026), new(2026, [Term], []), DoneAt);

    private static SourceSection Row(string section = "012") =>
        new(new(2026, Key.SemesterCode, Key.DetailSemesterCode, Key.CourseCode, section, "00"),
            "통계학", "김", "통계학과", "월\t14:00\n수 14:00", 116, 120, 999, "Enrollment/Quota") { ObservedAtUtc = PageAt };

    private static SourceSearchResult Result(IReadOnlyList<SourceSection> rows) =>
        new(rows, true, [], 1) { CompletedAtUtc = DoneAt };

    private static SourceDetails Details(IReadOnlyList<SourceMeeting> meetings) =>
        new(SnuIdentityMapper.ToSourceIdentity(Key), "통계학", "Statistics", "김", "Kim", "통계학과", 3m, 120, "Korean", "제한\n설명", meetings);

    private static void AssertParseFailure<T>(SourceRead<T> read) where T : class
    {
        Assert.Equal(SourceReadStatus.Failed, read.Status);
        Assert.Null(read.Data);
        Assert.Equal(SourceProblemKind.ParseChanged, Assert.Single(read.Problems).Kind);
        Assert.Equal("source_parse_changed", read.Problems[0].DiagnosticCode);
    }
}
