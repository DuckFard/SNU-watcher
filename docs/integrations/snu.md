# SNU public source adapter

M1 source-contract evidence was reviewed 2026-10-29 (Asia/Seoul); the M4 gateway and stored-policy implementation notes were updated 2026-11-01. The C# adapter is in `src/SnuWatcher.Infrastructure/Snu`. It is an anonymous reader of SNU's existing website interfaces, not an authorized developer API or a registration client. There is no login, student-cookie import, registration endpoint, or PDF feature. Milestone exit evidence remains in `docs/PROGRESS.md`.

## Verified website contracts

Bounded public requests and an actual compiled .NET adapter probe established:

| Interface | Observed request | Observed response |
| --- | --- | --- |
| `/sugang/cc/cc100ajax.action` | POST `srchOpenSchyy`, optional composite `srchOpenShtm`, `openUpDeptCd`, `openDeptCd` | HTTP 201 JSON; `SHTM` options, `UP_DEPT`, dependent `DEPT`; current-term metadata |
| `/sugang/cc/cc100InterfaceSrch.action` | POST `workType=S`, year, term selector, filters, `pageNo` and `srchCurrPage` | HTML course cards, ten per first result page, advertised `fnGotoPage(2)`; page-size request is not evidence of all results |
| `/sugang/cc/cc101ajax.action` | POST `workType=S`, `openSchyy`, `openShtmFg`, `openDetaShtmFg`, `sbjtCd`, `ltNo`, `sbjtSubhCd`; Referer is the official search page | JSON **served as `text/html; charset=utf-8`**, `LISTTAB01` metadata and `ltTime`/`ltType`/`ltRoom` arrays |

The detail media type is an endpoint-specific compatibility exception. The parser still requires a JSON object, the requested full identity, named metadata, and aligned meeting arrays. An HTML maintenance/login/gate response is rejected as `ParseChanged`; HTML is never rendered or executed. Catalog JSON and search HTML retain their own content-type checks.

The compiled .NET `SnuCourseSource.Create()` transport successfully read a fresh anonymous catalog with four terms and 43 combined college/department options for regular second semester, and a complete two-page `F32.102` search containing 12 sections. It located section `012` with detail-semester `U000300001` and subsubject `000`. The corrected compiled details probe read two meetings, an English course name, and capacity for that exact identity. This confirms those three transport/parser paths without importing browser state.

Nonempty dropdown responses verified `UP_DEPT`, `DEPT`, and `MJ` entries use `deptCd`, `deptKorNm`, and `deptEngNm`. Choosing college code `300` for the regular second semester returned a `DEPT` list containing department `326` (Statistics). A bounded compiled-adapter request selecting department `3348` returned two `MJ` options, alongside 30 colleges and 13 departments. Empty lists when a short/ambiguous term was posted are not a fallback catalog. Union-department options have compound values such as `856|856C`; they are not supported as ordinary department filter identifiers in this milestone.

## Identity and seasonal terms

The four source selector values observed in `SHTM` are:

| Term | Source selector |
| --- | --- |
| First semester, regular | `U000200001U000300001` |
| Second semester, regular | `U000200002U000300001` |
| Summer | `U000200001U000300002` |
| Winter | `U000200002U000300002` |

Names and selectors are read from SNU rather than constructed by the adapter. A `SourceSectionIdentity` preserves year, semester code, detail-semester code, course code, leading-zero section, and subsubject code. It deliberately does not merge aliases or treat names as identity. Search cards must agree with the requested year and the exact combined semester/detail-semester selector. A legacy short selector cannot silently match both regular and seasonal sections.

Legacy short-term migration and canonical alias policy belong to M3. Current evidence supports preserving full raw identities, not asserting that every combination is independently enrollable.

## Filters and details

`SourceSearchQuery` maps code/name/instructor to `srchSbjtCd`, `srchSbjtNm`, and `srchProfNm`; department hierarchy maps to `srchOpenUpDeptCd`, `srchOpenDeptCd`, and `srchOpenMjCd`. The mapping was inspected in SNU's public form and is covered by offline request-capture tests. A bounded compiled-adapter query combining `F32.102`, the source-reported title and instructor of section `012`, college `300`, and department `326` returned one verified section on one complete page. No arbitrary syllabus-text search is promised.

Three subsequent bounded queries using a deliberately nonmatching title, a deliberately nonmatching instructor, and a different valid department produced pages with no course cards. The initial parser correctly failed closed but did not recognize the site's empty-result convention. One additional inspected nonmatching-title response established HTTP 200, the expected search-result title, an empty `.course-info-list`, and the banner `0 건의 교과목이 검색되었습니다.`. Its minimized fixture was run RED before adding an explicit empty-result branch, then GREEN. A missing course-list container still fails closed.

Those initial negative pages preceded the parser fix. The title-only negative was not separately rerun; its inspected empty-page shape is covered by offline regression. Instructor and ordinary department filters were then checked independently through the corrected compiled adapter on 2026-10-29.

That follow-up used exactly eight HTTP requests: one catalog request, two baseline pages, and the five filtered pages below. It used the production `SnuCourseSource` with a fresh anonymous transport matching `Create()`, the default two-second pacing setting, a two-page result bound, and an additional handler that prevented a ninth network request. The selected term was source-confirmed regular second semester 2026 (`U000200002U000300001`). No raw response dump or instructor name is committed.

| Query within the `F32.102` cohort | Complete result | Pages | Identity comparison |
| --- | --- | --- | --- |
| Course code baseline | 12 sections | 2 | Sections `001`-`012` |
| Code plus one instructor reported by the baseline | 1 section | 1 | Section `003`, exactly that instructor's baseline identity set |
| Code plus deliberately nonmatching instructor | 0 sections | 1 | Corrected empty-result parser accepted a complete empty result |
| Code plus college `300`, department `326` | 12 sections | 2 | Exactly the same full raw identities as the baseline |
| Code plus college `300`, sibling department `322` | 0 sections | 1 | `322` was a valid option returned by the fresh source catalog |

These observations verify the tested instructor and ordinary department filters separately from the earlier combined-positive query, including their negative results. They do not establish every keyword, alternate-script name, union-department or major-filter semantic, or establish an authorized API. Major-option discovery remains distinct from verifying a major-filtered search.

Search rows retain the displayed `수강신청인원/정원(재학생)` or `Enrollment/Quota` label and separate total-enrollment value. Only a complete, nonnegative integer ratio token with an optional parenthesized count is accepted. Missing, negative, fractional, comma-formatted, overflow, or changed count text yields unknown counts, never inferred openings. Capacity zero and over-capacity enrollment remain representable without negative apparent seats. The adapter does not decide student eligibility or emit alerts.

M4 transport rows additionally retain the UTC instant when their own page body finished arriving, before decoding/disposal or later-page work. Query completion has a separate timestamp. Parser-only/manual source DTOs remain explicitly unstamped, and the application bridge must not invent live count freshness for them. Repeated identities keep the retained count and that count's receipt time together. Catalog-confirmed Application queries preserve the opaque term selector and every filter; scoped completion uses all six identity fields and never extends an already-issued scope with newly returned rows.

The pure completion mapper treats missing rows in complete results as `NotFound`, and missing rows in partial results as `Incomplete`. Valid rows from earlier pages remain usable after a later-page error. Invalid counts or quota labels preserve the last valid baseline through the observation preparer. A row timestamp before its issued attempt is withheld conservatively; that extra wall-clock guard is not proof of request provenance and does not replace persisted attempt/generation ordering. The implemented `SnuCourseGateway` caches bounded complete catalog/detail metadata only, coalesces equivalent in-flight interactive queries, and starts a fresh source operation for each watch search. It never satisfies a newly issued watch attempt from a pre-issuance UI request or cached count. All catalog, search-page and detail operations share one hosted source and request dispatcher.

Detail parsing retains Korean/English names and instructor labels, department, credits, capacity, instruction language, remarks/restrictions, and original meeting strings. Missing optional metadata remains null. Meetings remain source strings; exact weekday/time normalization belongs to the application layer after corresponding fixtures exist. No registration deep link is invented; current safe browser fallback is the official registration homepage. Genie remains an external-browser link, with no SSO or API dependency.

## Failure and resource contract

- Production hosting must use `SnuCourseSource.CreateAsync(ISourcePolicyStore, ...)`, which restores saved source restrictions before any HTTP. Its anonymous client disables redirects, uses only the three fixed HTTPS SNU routes, and holds any anonymous session cookie in memory. The public constructor and `Create()` remain explicitly unhosted diagnostic compatibility; they are not a production-host fallback. A linked default 20-second deadline bounds the request and body read, not just arrival of response headers; external cancellation remains distinct. No browser cookies or credentials are read.
- Every response is checked against the exact requested URI before content parsing. Redirects/access denial become `AccessRestricted`; 429 becomes `RateLimited`, preserving integer or HTTP-date `RetryAfter`.
- Production requests share one dispatcher, one in-flight callback, and at least two seconds between actual HTTP starts, including pagination. This is an application default, not an SNU-approved polling rate. A private offline test seam alone permits zero spacing. Interactive work and due watches have bounded fair admission; source-wide cooldown/access restrictions apply before the next callback can send. The adapter itself is not a polling scheduler.
- Maximum response size is 2,000,000 decoded bytes by default, enforced both from a declared length and while streaming. Maximum advertised page is 25 by default. Refine filters after `LimitReached`; do not remove bounds to fetch the whole catalog.
- A failed first page throws a typed `SnuSourceException`. A failed later page returns the safely parsed rows with `IsComplete=false`, the typed failure and any cooldown. An absent section from that partial result is unknown, not full/not-found.
- External cancellation propagates as cancellation; network timeouts and connection failures have generic source messages that do not expose transport internals.
- Input identifiers and text lengths are bounded. Public remote strings are data, not commands or trusted navigation. The transport adds no file import or executable invocation; its hosted policy port persists bounded source request/result/restriction facts in the application's store, without storing cookies or credentials.

Known 429, access-denied and supported 503 `Retry-After` outcomes are applied inside the held request callback and durably recorded before release, including when the caller cancels after the response is known. Both delta and HTTP-date delays are honored without clamping a longer server delay to local backoff. The running queue retains a monotonic floor in addition to persisted UTC evidence. A failed policy write blocks new HTTP until the identical known result is replayed through explicit recovery; ordinary access resume cannot clear that storage fault. Startup also applies a two-second local floor and, for an unresolved issued intent, a conservative 60-second local recovery floor. This is not a fabricated server response and cannot reconstruct an arbitrary lost `Retry-After` value.

## Fixtures and verification

An opt-in live check on 2026-11-01 used exactly two catalog POSTs in total. The store-backed gateway's first attempt failed closed as `ParseChanged`; a separate matching, bounded structural diagnostic returned HTTP 200 with no Content-Type and a zero-byte body. No course search or detail request was made, so the current catalog/search/detail happy path was not reverified. The upstream cause is unknown; this is not evidence of a maintenance template, and the parser was not relaxed. An offline regression reproduces the empty response through the gateway and stored source, retaining one failed request/result without fabricating catalog data, availability, watches or outbox entries.

`tests/SnuWatcher.Integration.Tests/Fixtures` contains minimized, synthetic course metadata following observed shapes. It contains no cookies, student IDs, login state, raw response dumps, or supplied private documents. Public term/code values are retained; instructor text is replaced with an example. Department labels reflect the verified source field names. Default tests are offline and never crawl SNU.

TDD evidence: the initial source tests were run against throwing stubs, producing 16 expected behavior failures and one passing identity-validation test. Additional malformed-count, partial-cooldown, short-selector and legacy-detail-media regressions were also observed failing before the fixes. The parent development report records the final aggregate test/coverage run.

Run this project's offline suite using the repository's workspace SDK wrapper or `dotnet test tests/SnuWatcher.Integration.Tests`. The repository-wide foundation script additionally enforces at least 80% line and branch coverage for the adapter assembly. A mocked HTTP result is not a live data verification; a live result is not institutional permission for a particular polling rate.

## Remaining source evidence before broader distribution

- Major-filtered searches remain unverified; confirm them with bounded live checks before enabling a major-specific UI. The separately verified instructor/ordinary-department cases above cover the tested `F32.102` cohort, not every alternate-script name, union department, or catalog category.
- Add fixtures before enabling new layouts, count labels, quota categories, or pagination helpers. Unknown source changes must remain visible, not broaden a count regex.
- The current maintenance HTML fixture is synthetic; no exact official HTTP-200 maintenance fingerprint has been established. Unknown HTML remains a parse/source failure. Shared queue support for declared maintenance or a typed verified classifier must not be reported as automatic recognition of an unverified SNU template.
- Full raw identities are the implemented identity boundary and do not require alias merging. Cross-term aliases remain unsupported without corresponding offering/subsubject evidence; the four selector labels are not alias proof.
- Exact course registration/syllabus links and Genie deep links remain behind compatibility checks. External-browser homepage links are the fallback.
- No published approved numeric automated polling rate was established. Obtain operator guidance before a broad public pilot; lack of throttling in these bounded reads does not establish permission.

Sources: [SNU public search and notice page](https://sugang.snu.ac.kr/sugang/co/co100.action), [official registration manual](https://sugang.snu.ac.kr/kor/guide/manual.pdf), [AngleSharp official NuGet package](https://www.nuget.org/packages/AngleSharp). The source actions are observed internal site behavior and may change without notice.
