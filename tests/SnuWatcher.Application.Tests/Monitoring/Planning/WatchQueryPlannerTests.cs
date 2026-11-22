using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Planning;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Planning;

public sealed class WatchQueryPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OverlappingIndividualAndGroupWatchesShareOneExactCourseQuery()
    {
        var first = Key(section: "002");
        var second = Key(section: "012");
        var watches = new[] { Watch([second]), Watch([second, first], label: "Any writing class") };

        var plan = WatchQueryPlanner.Plan(watches, [Catalog()], Now);

        var query = Assert.Single(plan.Queries);
        Assert.Equal(new CourseSearchQuery(2026, "21", "F32.102"), query.Query);
        Assert.Equal<SectionKey>([first, second], query.Scope);
        Assert.Null(query.Query.Name);
        Assert.Null(query.Query.Instructor);
        Assert.Null(query.Query.UpDepartment);
        Assert.Null(query.Query.Department);
        Assert.Equal<SectionKey>(query.Scope, Assert.Single(query.IssueScopes));
        Assert.Empty(plan.BlockedSections);
    }

    [Fact]
    public void OneGroupCanPlanSeveralCoursesWithoutDependingOnItsInitialNoticePreference()
    {
        var first = Key();
        var second = Key(course: "F32.102L", section: "002");

        var plan = WatchQueryPlanner.Plan([Watch([second, first], initialNoticeEnabled: false)], [Catalog()], Now);

        Assert.Equal(2, plan.Queries.Length);
        Assert.Equal<SectionKey>([first], plan.Queries[0].Scope);
        Assert.Equal<SectionKey>([second], plan.Queries[1].Scope);
    }

    [Fact]
    public void AddingASectionChangesOnlyTheNewScopeAndNeverMutatesStoreSnapshots()
    {
        var first = Key(section: "002");
        var second = Key(section: "012");
        var original = Watch([first]);
        var watches = new List<SubscriptionSnapshot> { original };
        var catalogs = new List<CourseCatalogData> { Catalog() };
        var before = WatchQueryPlanner.Plan(watches, catalogs, Now);
        watches.Add(Watch([second], label: "A different label"));
        var after = WatchQueryPlanner.Plan(watches, catalogs, Now);
        watches.Clear();
        catalogs.Clear();

        Assert.Equal<SectionKey>([first], Assert.Single(before.Queries).Scope);
        Assert.Equal<SectionKey>([first, second], Assert.Single(after.Queries).Scope);
        Assert.Equal(before.Queries[0].Query, after.Queries[0].Query);
        Assert.Equal(7, original.ActivationGeneration);
        Assert.Equal(3, original.DefinitionRevision);
        Assert.Equal(17, Assert.Single(original.Members).ActivationWatermark);
        Assert.Equal(24, original.Members[0].InitializedByAttemptOrdinal);
    }

    [Fact]
    public void FullIdentityAndExactCourseSpellingRemainDistinctAcrossStableSortedQueries()
    {
        var keys = new[]
        {
            Key(subsubject: "001"), Key(), Key(year: 2025), Key(detail: "2"),
            Key(course: "F32.102L", section: "002"), Key(course: "f32.102"),
        };
        var watches = keys.Select(key => Watch([key])).ToArray();
        var catalogs = new[] { Catalog(2026, "22", "21"), Catalog(2025) };

        var forward = WatchQueryPlanner.Plan(watches, catalogs, Now);
        var reversed = WatchQueryPlanner.Plan(watches.Reverse(), catalogs.Reverse(), Now);

        Assert.Equal(5, forward.Queries.Length);
        Assert.Equal<SectionKey>(keys.OrderBy(key => key.Year).ThenBy(key => key.SemesterCode, StringComparer.Ordinal)
            .ThenBy(key => key.DetailSemesterCode, StringComparer.Ordinal).ThenBy(key => key.CourseCode, StringComparer.Ordinal)
            .ThenBy(key => key.Section, StringComparer.Ordinal).ThenBy(key => key.SubsubjectCode, StringComparer.Ordinal),
            forward.Queries.SelectMany(query => query.Scope));
        Assert.Equal(forward.Queries.Select(query => query.Query), reversed.Queries.Select(query => query.Query));
        Assert.Equal<SectionKey>(forward.Queries.SelectMany(query => query.Scope), reversed.Queries.SelectMany(query => query.Scope));
        Assert.Equal<SectionKey>([Key(), Key(subsubject: "001")],
            forward.Queries.Single(query => query.Query == new CourseSearchQuery(2026, "21", "F32.102")).Scope);
        Assert.All(forward.Queries.SelectMany(query => query.Scope), key => Assert.Equal(3, key.Section.Length));
    }

    [Fact]
    public void MissingYearAndUnreportedTermAreVisibleAsFixedBlockedReasons()
    {
        var missingYear = Key(year: 2027);
        var missingTerm = Key(detail: "2");
        var confirmed = Key();
        var watches = new[] { Watch([missingYear]), Watch([missingTerm]), Watch([confirmed]), Watch([missingTerm]) };

        var plan = WatchQueryPlanner.Plan(watches, [Catalog()], Now);

        Assert.Single(plan.Queries);
        Assert.Collection(plan.BlockedSections,
            blocked => { Assert.Equal(missingTerm, blocked.Key); Assert.Equal("term_unconfirmed", blocked.ReasonCode); },
            blocked => { Assert.Equal(missingYear, blocked.Key); Assert.Equal("catalog_unavailable", blocked.ReasonCode); });
    }

    [Fact]
    public void CatalogLabelsAndLegacyTermAliasesCannotInventAReportedSelector()
    {
        var catalog = new CourseCatalogData(new CatalogQuery(2026),
            [new CatalogTerm(2026, "2", "Second semester", "Regular 2"), new CatalogTerm(2026, "opaque", "21", null)], []);

        var plan = WatchQueryPlanner.Plan([Watch([Key()])], [catalog], Now);

        Assert.Empty(plan.Queries);
        Assert.Equal("term_unconfirmed", Assert.Single(plan.BlockedSections).ReasonCode);
    }

    [Fact]
    public void OpaqueReportedSelectorDoesNotAliasDifferentSixPartScopeKeys()
    {
        var first = Key(semester: "1", detail: "21");
        var second = Key(semester: "12", detail: "1");

        var plan = WatchQueryPlanner.Plan([Watch([second]), Watch([first])], [Catalog(2026, "121")], Now);

        var query = Assert.Single(plan.Queries);
        Assert.Equal("121", query.Query.TermSelector);
        Assert.Equal<SectionKey>([first, second], query.Scope);
        Assert.Collection(query.IssueScopes,
            scope => Assert.Equal<SectionKey>([first], scope),
            scope => Assert.Equal<SectionKey>([second], scope));
        Assert.All(query.IssueScopes, scope => Assert.Equal<SectionKey>(scope, new CheckIssue(Now, scope).Scope));
    }

    [Fact]
    public void LargeLogicalQueryKeepsEverySectionWhilePartitioningThePersistenceIssueBudget()
    {
        var keys = Enumerable.Range(0, 2001).Select(index => Key(section: (index % 1000).ToString("D3"),
            subsubject: (index / 1000).ToString("D3"))).ToArray();
        var watches = keys.Select(key => Watch([key]));

        var query = Assert.Single(WatchQueryPlanner.Plan(watches, [Catalog()], Now, 2001).Queries);

        Assert.Equal(2001, query.Scope.Length);
        Assert.Equal(new[] { PersistenceLimits.MaxSections, 1 }, query.IssueScopes.Select(scope => scope.Length));
        Assert.Equal<SectionKey>(query.Scope, query.IssueScopes.SelectMany(scope => scope));
        Assert.All(query.IssueScopes, scope => Assert.Equal<SectionKey>(scope, new CheckIssue(Now, scope).Scope));
    }

    [Theory]
    [InlineData(WatchStatus.Paused)]
    [InlineData(WatchStatus.Expired)]
    [InlineData(WatchStatus.Removed)]
    public void InactiveWatchesNeitherQueryNorBecomeBlocked(WatchStatus status)
    {
        var plan = WatchQueryPlanner.Plan([Watch([Key()], status)], [], Now);
        Assert.Empty(plan.Queries);
        Assert.Empty(plan.BlockedSections);
    }

    [Fact]
    public void UtcExpiryIsExclusiveAndUsesTheSameInstantAcrossOffsets()
    {
        var expiry = new WatchExpiry(new DateOnly(2026, 10, 31), "Asia/Seoul", Now);
        var watch = Watch([Key()], expiry: expiry);

        Assert.Single(WatchQueryPlanner.Plan([watch], [Catalog()], Now.AddTicks(-1)).Queries);
        Assert.Empty(WatchQueryPlanner.Plan([watch], [Catalog()], Now.ToOffset(TimeSpan.FromHours(9))).Queries);
        Assert.Empty(WatchQueryPlanner.Plan([watch], [Catalog()], Now.AddTicks(1)).Queries);
        Assert.Empty(WatchQueryPlanner.Plan([watch], [], Now).BlockedSections);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10001)]
    public void UniqueSectionResourceLimitMustBeWithinItsSupportedRange(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WatchQueryPlanner.Plan([], [], Now, limit));

    [Fact]
    public void UniqueLimitCountsTheActiveUnionIncludingBlockedKeysWithoutSilentTruncation()
    {
        var first = Key();
        var second = Key(section: "013");
        var watches = new[] { Watch([first]), Watch([first, second]) };

        var error = Assert.Throws<WatchQueryPlanLimitException>(() => WatchQueryPlanner.Plan(watches, [], Now, 1));
        Assert.Equal("watch_plan_limit_exceeded", error.Code);
        Assert.Equal(1, error.MaxUniqueSections);
        Assert.Equal(2, WatchQueryPlanner.Plan(watches, [], Now, 2).BlockedSections.Length);
        Assert.Single(WatchQueryPlanner.Plan([watches[0], Watch([first]), Watch([second], WatchStatus.Paused)], [Catalog()], Now, 1).Queries);
        Assert.Single(WatchQueryPlanner.Plan([watches[0]], [Catalog()], Now, 10000).Queries);
    }

    [Fact]
    public void DefaultLimitIsTwoThousandUniqueSectionsAndFailsExplicitlyOnTheNext()
    {
        var watches = Enumerable.Range(0, 2001)
            .Select(index => Watch([Key(course: $"C{index / 1000}", section: (index % 1000).ToString("D3"))])).ToArray();

        Assert.Equal(2000, Assert.Throws<WatchQueryPlanLimitException>(() =>
            WatchQueryPlanner.Plan(watches, [Catalog()], Now)).MaxUniqueSections);
        Assert.Equal(2001, WatchQueryPlanner.Plan(watches, [Catalog()], Now, 2001).Queries.Sum(query => query.Scope.Length));
    }

    [Fact]
    public void AmbiguousDuplicateYearCatalogsAreRejectedInsteadOfSelectingOne()
    {
        Assert.Throws<ArgumentException>(() => WatchQueryPlanner.Plan([Watch([Key()])], [Catalog(), Catalog(2026, "22")], Now));
        Assert.Throws<ArgumentException>(() => WatchQueryPlanner.Plan([], [Catalog(), Catalog()], Now));
    }

    [Fact]
    public void NullAndUninitializedInputsAreRejectedAtThePlannerBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => WatchQueryPlanner.Plan(null!, [], Now));
        Assert.Throws<ArgumentNullException>(() => WatchQueryPlanner.Plan([], null!, Now));
        Assert.Throws<ArgumentException>(() => WatchQueryPlanner.Plan([null!], [], Now));
        Assert.Throws<ArgumentException>(() => WatchQueryPlanner.Plan([], [null!], Now));
        Assert.Throws<ArgumentException>(() => WatchQueryPlanner.Plan(default(ImmutableArray<SubscriptionSnapshot>), [], Now));
        Assert.Throws<ArgumentException>(() => WatchQueryPlanner.Plan([], default(ImmutableArray<CourseCatalogData>), Now));
    }

    [Fact]
    public void InputEnumerationIsBoundedEvenWhenEverySubscriptionRepeatsOneSection()
    {
        var reads = 0;
        var watch = Watch([Key()]);
        IEnumerable<SubscriptionSnapshot> TooMany()
        {
            while (true) { reads++; yield return watch; }
        }

        Assert.Throws<ArgumentException>(() => WatchQueryPlanner.Plan(TooMany(), [], Now));
        Assert.Equal(10001, reads);
        var catalogReads = 0;
        var catalog = Catalog();
        IEnumerable<CourseCatalogData> TooManyCatalogs()
        {
            while (true) { catalogReads++; yield return catalog; }
        }
        Assert.Throws<ArgumentException>(() => WatchQueryPlanner.Plan([], TooManyCatalogs(), Now));
        Assert.Equal(189, catalogReads);
    }

    private static SectionKey Key(int year = 2026, string semester = "2", string detail = "1", string course = "F32.102",
        string section = "012", string subsubject = "000") => new(year, semester, detail, course, section, subsubject);

    private static CourseCatalogData Catalog(int year = 2026, params string[] selectors) => new(new CatalogQuery(year),
        (selectors.Length == 0 ? new[] { "21" } : selectors).Select(selector => new CatalogTerm(year, selector, "Term", null)), []);

    private static SubscriptionSnapshot Watch(SectionKey[] keys, WatchStatus status = WatchStatus.Active,
        WatchExpiry? expiry = null, string label = "A watch", bool initialNoticeEnabled = true) => new(Guid.NewGuid(), 3, 7,
        keys.Length == 1 ? SubscriptionKind.Section : SubscriptionKind.Group, label, status, initialNoticeEnabled, expiry, Now.AddDays(-1),
        keys.Select(key => new MemberSnapshot(key, 17, 24)));
}
