# SNU Course Watcher desktop implementation plan

Status: implementation authorized on 2026-10-28, Asia/Seoul. M1 source contracts and M2 native feasibility are in progress; completion and evidence are recorded in `docs/PROGRESS.md`. The full student desktop application and production installer are not complete. Existing CLI runtime configuration, state, and running processes remain unchanged.

Canonical repository: [DuckFard/SNU-watcher](https://github.com/DuckFard/SNU-watcher). Project license: MIT, copyright 2026 DuckFard. The repository preserves the Python prototype as a legacy reference alongside the new foundation work. Development candidates are not production releases.

## 1. Capability and decisions

Students download a Windows installer from GitHub, find SNU courses, select exact sections or groups, and receive identifiable, clickable alerts when apparent free seats increase. The application manages monitoring, expiry, freshness, and failures through a familiar interface. Students add sections through course search or exact identifiers. Registration takes place in the student's normal browser on the official site.

Recommended implementation: C# on .NET 10, WPF with MVVM, SQLite, and Windows App SDK local app notifications. Ship a self-contained installed application in a signed Inno Setup installer named provisionally `SnuCourseWatcher-<version>-windows-x64-Setup.exe`. Treat the product name as provisional until the owner chooses it.

Defaults to carry into implementation unless the owner changes them:

- Windows 11 x64 is the first supported platform. Korean and English UI; preserve original course titles in both when the source supplies them. ARM64 and other operating systems are later scope.
- All retained requested capabilities are in v1; the owner removed PDF import from scope. No mandatory cloud account, server, subscription, or API key. No student credentials collected.
- Re-create the familiar SNU information layout in original WPF components. Search and counts use SNU data; official pages and Genie open externally.
- A course group is an explicitly confirmed set of sections in a specific term. Discovering more sections proposes additions; it never silently enrolls a different course in the watch list.
- Default alerts include both full-to-open transitions and increases while already open, preserving the user's corrected requirement from the CLI.
- Use a target refresh schedule with shared request pacing. The interface reports actual observation age rather than promising a guaranteed interval.
- Deliver an installer EXE, with the runtime files installed behind it. A portable, zero-install single binary is not the v1 acceptance target.

Public release remains subject to the source-access and release decisions in section 13. They do not prevent private implementation after approval of this plan.

### Framework comparison

| Option | Advantages for this application | Tradeoff | Decision |
| --- | --- | --- | --- |
| C# / .NET 10 / WPF | One application language for UI, parsing, persistence, and scheduler; direct Windows notification activation; mature binding, localization, and accessibility | Windows only; port the small Python engine | Recommended for the requested Windows EXE |
| Tauri 2 / React / TypeScript / Rust | Strong web UI ecosystem and smaller shell than a bundled browser engine; attractive if macOS becomes a requirement | More languages; Windows notification action support needs additional validation/native work | Alternative if cross-platform becomes a launch requirement |
| PySide6 / Python | Most direct source reuse; strong desktop widgets | Packaging and native notification/click integration still need platform work; assess Qt distribution obligations | Good prototype route, less compelling than C# for this Windows release |
| Electron / TypeScript | Convenient web UI and desktop ecosystem | Bundled browser/runtime increases distribution and update surface; existing Python still needs a bridge or port | No product advantage sufficient to select it here |

WPF is Windows-only [S1]. Microsoft documents local notifications and activation for WPF through `AppNotificationManager` [S2, S3]. Tauri's documentation currently limits its notification Actions API to mobile and requires installed apps for Windows notifications [S4]; this does not mean a native Windows extension is impossible. The comparison is an architectural judgment, not a measured performance benchmark.

### Proposed dependencies and boundaries

| Responsibility | Planned dependency | Boundary |
| --- | --- | --- |
| UI | WPF, CommunityToolkit.Mvvm | ViewModels call application services; no network or SQL in views |
| Network / HTML | HttpClient, AngleSharp | SNU adapter owns forms, sessions, response validation, and parsing |
| Database | Microsoft.Data.Sqlite | Parameterized SQL; versioned transactional migrations |
| Desktop notifications | Microsoft.WindowsAppSDK | App-owned identity, activation, delivery status, sound preferences |
| Tray | Windows Forms NotifyIcon behind a small WPF-facing adapter | Explicit close-to-tray and Quit semantics |
| Tests | xUnit, fake HTTP/clock, Coverlet; Windows UI automation such as FlaUI | Domain tests offline; installer/notification checks on Windows |
| Installer | Inno Setup | Carry the complete publish folder; review licenses for selected versions |

Pin supported stable versions for implementation, record license/redistribution obligations, and lock dependencies. The owner has authorized development; package restore and foundation projects are now in scope.

## 2. Evidence and what it establishes

### Existing prototype

The current Python CLI is useful as a behavioral reference, fixture collection, and migration source. It should not simply be placed behind a UI unchanged.

Preserve its leading-zero section identity, exact-match parsing, bounded pagination, grouped requests, additional-free-seat alert rule, and deterministic test scenarios. Port these into C# with shared golden cases.

Replace these behaviors before desktop release:

- `service.py` sleeps after a full sweep; its configured interval is not a per-section check guarantee.
- `state.py` preserves the last good state on unknown input, but the runtime lacks a separate visible latest-attempt state. An unchanged old timestamp can hide repeated failures.
- `watch_forever` discards polling outcomes. An empty redirected error log is not proof of healthy polling.
- `CompositeNotifier` acknowledges if any channel succeeds, including console output. A log write can conceal desktop delivery failure.
- The toast uses another application's PowerShell identity and has no clickable registration action.
- TOML-only management, a blocking loop, one state table, and no process lock cannot support live edits, groups, expiry, migrations, or reliable background lifecycle.
- The README no longer describes all implemented behavior accurately. Documentation must be updated alongside changes.

These are source-inspection findings, not a claim that any currently running process was tested in this planning session.

### Current SNU observations

Bounded public inspection found search form fields for year, term, course code, course name, instructor, and department/major hierarchy. `cc100InterfaceSrch.action` still returned course cards and pagination; requesting a large page size did not remove pagination. The site exposes a public detail response at `cc101ajax.action` with metadata and meeting arrays while reporting `MISC.isLogin=false` [S5].

These are observed internal website interfaces, not a documented supported developer API. Re-check them through the gate in section 11 before implementing against them.

As of this review, the site's default term was 2026 winter, with selector value `U000200002U000300002`. The old CLI's regular-term assumptions are insufficient. Preserve source fields `openShtmFg`, `openDetaShtmFg`, and `sbjtSubhCd`; establish their identity/alias semantics with fixtures rather than assuming all are independent enrollable offerings.

SNU's misconduct notice concerns macro-assisted registration attempts and course transfers/sales. It does not specify an approved automated polling interval [S6]. The old 30-second floor was our application choice, not SNU approval. The current winter notice also identifies periods in which the whole site or search features are restricted [S7]. Plan explicit source-limited and maintenance states.

## 3. Feature contract

| User requirement | v1 behavior | Completion evidence |
| --- | --- | --- |
| 1. Find courses | Select year/term; search code, title, instructor, department; keyword means matching title/instructor/catalog fields, not unverified full-syllabus search | Each filter and pagination verified against fixtures and a small live check |
| 2. Details and Genie | Detail panel shows identifiers, instructor(s), meetings, room, department, credits, quota labels, source remarks; View in Genie opens browser | Missing fields display unavailable; Genie fallback works without app credentials |
| 3. Availability | Registered/quota and derived apparent seats, full/available badge, separate total enrollment where present | Correct source-label mapping; no eligibility claim |
| 4. Watch exact section | Watch action stores confirmed term and section; scheduler updates it while active | Exact identity survives restart and page-two lookup |
| 5. Popup alert | Native Windows popup and optional sound; in-app history; failed delivery visibly retryable | Installed clean-PC banner, sound, and failure tests |
| 6. Register from alert | Body click/Open registration opens approved official URL in default browser; View section opens app detail; Copy identifier button in app | Activation works with window visible, in tray, and after process exit |
| 7. Manage watches | Add, pause, resume, remove; bulk operations on a confirmed selection | Changes apply immediately and survive restart |
| 8 and 12. Freshness | Last attempt, last successful check, age, next estimated check, delayed/unavailable indicators | Failed attempts never advance last-success time |
| 9. No reservation | Inline message: an alert does not reserve a seat; availability may change before registration | Present in onboarding, course detail, and notification context |
| 11. Any-section groups | Choose all or selected sections under one course, or explicitly combine courses in a named group | Group coverage and matched sections visible; overlapping watches deduplicated |
| 13. Source outages | Show offline, rate limited, maintenance, limited search, parse changed, and not-found states separately | Fault fixtures produce actionable messages and no false openings |
| 14. Watch duration | Until date/time, selected registration-window end, or until manually stopped | Expiry stops future scheduling and delivery even during in-flight requests |
| 15. Small supporting features | Tray operation, test notification, alert history, copy code/section, local watch backup/export, optional start at sign-in | These support the requested workflow rather than adding another product |

PDF import and OCR are excluded by the owner's scope decision; do not add parsers, import screens, candidate models, or PDF dependencies. No auto-registration, CAPTCHA interaction, credential collection, silent SSO integration, paid service, or cloud watch synchronization in v1. Mobile push, predictive availability, a complete timetable planner, and a shared backend are later scope.

## 4. Interface and student journeys

### Main window

Use four navigation entries: Find courses, My watches, Alerts, Settings. A persistent header shows academic year/term and overall source health; a footer shows monitoring state and estimated queue delay. Course search uses the familiar SNU labels, compact rows, blue enrollment numbers, and expandable details in app-owned styling. Mark it as an unofficial companion. Do not copy SNU logos, stylesheets, or remote scripts as an application dependency.

Find courses has a code/name/keyword search box plus instructor and department filters. Submit on Enter or Search; debounce suggestions from already-cached results. Never fire a remote request per keystroke. Show result loading, empty, partial, and failed states separately. A row identifies code, section, title, instructor, schedule, quota, freshness, and Watch. The detail panel provides location, source restrictions, official-page links, and group selection.

My watches is grouped by term and named group. It shows active/paused/expired state, last good count, age, latest attempt result, next check estimate, expiry, and controls. A group header can say `2 sections with openings; 18/20 checked successfully`; it cannot claim all sections are full after a partial failure. Counts are section-level observations, not guaranteed eligible seats or unique students.

Alerts records observation time, count change, affected sections/groups, delivery state, and actions. Settings includes language, refresh profile, sound, test notification, close behavior, optional sign-in launch, retention, diagnostics export, and release link. “API accepted” and “student confirmed seeing test” are separate statuses.

### First run

1. Explain the companion role, local operation, no seat reservation, and that the PC must stay awake.
2. Choose language and term from SNU's available terms; cache terms for an offline launch.
3. Offer course search, entry of an exact course code/section, or migration from the existing watcher. Exact identifiers still resolve against the chosen term before monitoring begins.
4. Show a test notification and ask whether it was visible. Provide the Windows notification-settings link and in-app troubleshooting if it was not.
5. Explain close-to-tray behavior; optional sign-in launch starts disabled.

### Any-section groups

For “any class of College Writing,” first resolve the academic course identity in the chosen term. Show all matching sections with instructors/times and let the student select all or a subset. If “College Writing” covers distinct catalog codes, show them separately and allow an explicitly named multi-course group.

Store a confirmed membership snapshot. A low-frequency catalog refresh may show “2 new sections found - review”; it does not add them automatically in v1. Group pause and expiry apply to that membership subscription. A section retained by another active subscription may still be checked; it produces no duplicate event. Removing a group does not remove an independent watch on the same section.

## 5. Architecture and ownership

One ordinary, unelevated application process owns monitoring. Closing the window can leave it in the tray; Quit ends monitoring. Second launch foregrounds the existing instance or forwards a validated notification activation. This prevents the duplicate process problem encountered with the CLI. No Windows service or local HTTP server is required.

Planned desktop structure in DuckFard/SNU-watcher; only docs and the legacy reference are saved during planning:

```text
src/
  SnuWatcher.Desktop/          WPF views, ViewModels, resources, composition
  SnuWatcher.Core/             immutable models, alert reducer, freshness rules
  SnuWatcher.Application/      search/watch/group use cases, scheduler, migration
  SnuWatcher.Infrastructure/   SNU adapters, SQLite, diagnostics
  SnuWatcher.Windows/          notifications, activation, tray, process ownership
tests/
  SnuWatcher.Core.Tests/
  SnuWatcher.Integration.Tests/
  SnuWatcher.Desktop.Tests/
  fixtures/                   sanitized HTML/JSON and golden cases
installer/                    Inno Setup definition and release assets
docs/                         product contract, adapter notes, decisions, runbooks
.github/workflows/            build/test, release, dependency checks
legacy/                       reference or provenance for the old CLI
```

These are the target modules; implement them in the gated milestone order below. Keep the existing CLI intact until migration and desktop acceptance pass.

Core interface responsibilities:

- `ICourseCatalogSource`: list terms/departments, search with filters/page cursor, get details, fetch watched observations with complete/partial result metadata.
- `IWatchRepository`: transactional watch/group/settings operations, optimistic revision checking, migration and backup.
- `ILegacyMigrationService`: read legacy TOML/SQLite definitions, confirm source terms, and migrate idempotently without modifying originals. Migration and local watch backup are Settings actions, not a document-import screen.
- `ICheckScheduler`: due work, query coalescing, cancellation, source-wide pacing/cooldown, wake/offline recovery.
- `IAlertPolicy`: pure transition from last valid observation and new valid observation to opening events.
- `INotificationChannel`: submit an event and report a channel-specific outcome; activation is a separate Windows concern.
- `IOfficialLinkService`: build/validate browser destinations from trusted source identifiers, with homepage fallback.
- `IClock`: monotonic scheduling plus UTC wall timestamps for repeatable tests.

UI work runs on the dispatcher; network and source parsing are asynchronous/cancellable. Business rules remain independent of WPF and the website's HTML.

## 6. Data model and persistence

Use a versioned SQLite database under `%LOCALAPPDATA%/<publisher>/<app>/`. Keep logs, caches, settings, and migrations out of the application installation directory. The publisher/app directory is finalized with the installer identity.

| Entity | Essential content |
| --- | --- |
| Term | Year, display name, source selector, regular/detail semester IDs, discovery time |
| Course / Section | Canonical key, original source IDs, names, departments, instructors, credits, source URLs, metadata timestamp |
| Meeting | Section, day, start/end when explicit, room/building, original source text |
| WatchSubscription | Section or group, active/paused/expired state, expiry instant, alert preferences, revision |
| Group / GroupMember | Label, explicit course/section membership, confirmed term, discovery/review state |
| Observation | Section, reported enrollment/quota, separate total/other quota labels, apparent seats, fetch time, source/cache provenance |
| CheckAttempt | Start/end, result category, duration, HTTP status, typed error, retry time, query coverage |
| AlertEvent / DeliveryAttempt | Durable event ID, previous/new count, observation time, current validity, matching subscriptions, per-channel outcome/retries |
| Settings / SchemaVersion | Versioned preferences, migration status, diagnostics retention |

Establish canonical identity in the source-contract gate: minimum year + term + course code + section; preserve subterm/subsubject values and aliases where needed. Never force the old semester regex onto seasonal terms. Course names are display/search data, not primary keys. Empty identifiers and partially parsed rows cannot form verified watches.

Write new observation, comparison baseline, event, and subscriptions-to-event associations atomically. Dispatch from a durable outbox after commit. Crash recovery must not lose a committed event. Delivery is at-least-once with stable event/Windows tags and deduplication; do not claim exactly-once popups across crashes.

Default retention proposal: detailed check attempts 7 days; count-change observations and alert history 30 days; current watches/settings until removed. Rotate logs and cap disk use. Diagnostics export strips local paths/student data and is opt-in. Authenticated browser data never enters diagnostics.

Migration reads a consistent snapshot of legacy TOML/SQLite without modifying originals, deduplicates imports, and maps terms through the new resolver. Keep original observation timestamps and mark old baselines stale. Require a fresh check before deciding initial availability. Migration failure rolls back the new database transaction. Do not stop or alter another program without an explicit migration action.

## 7. SNU adapter contract

`cc100InterfaceSrch.action` search currently uses `srchOpenSchyy`, `srchOpenShtm`, `srchSbjtCd`, `srchSbjtNm`, `srchProfNm`, and department fields such as `srchOpenUpDeptCd`, `srchOpenDeptCd`, `srchOpenMjCd`. Obtain current values from source forms. Normalize paging fields according to verified form behavior; bound pages, bytes, time, and work.

Observed detail requests to `cc101ajax.action` use `workType`, `openSchyy`, `openShtmFg`, `openDetaShtmFg`, `sbjtCd`, `ltNo`, `sbjtSubhCd`, and optional professor context. Responses contain `ltTime`, `ltType`, `ltRoom`, `LISTTAB01`, and service status. Use detail for metadata; keep the observed public enrollment/quota labels distinct from any total enrollment. A missing field becomes unavailable, not a fabricated value.

Anonymous site-session cookies may exist in memory for normal public navigation. Do not import browser cookies, persist authentication cookies, or log session values. Follow only validated official origins/routes, enforce TLS, time/size limits, and validate redirects as well as response content. Remote text is rendered as text; no executing website scripts inside the desktop app.

Use bounded metadata caching and on-demand searches; do not repeatedly download the entire university catalog. One search response can satisfy several sections/groups. Retrieve extra details only when displayed, added to a watch, or needed to refresh changed metadata. Live counts have their own freshness budget.

A response is successful only if the intended term/query/section can be verified and required count fields are valid. A matching row on a completed page can update that section even if another page fails; missing sections from an incomplete query remain unknown. Group completeness stays partial. Never interpret an incomplete search as zero matches, a removed section, or all-full.

For Genie v1, link to the external site and provide a copyable code. Official information confirms its course/instructor search and mySNU access [S9]. An exact course link may be enabled only after the corresponding public link pattern is verified. Genie API/SSO and cross-service ID mapping are not established dependencies.

## 8. Monitoring, alert, and health rules

### Scheduling and rate

Use per-query monotonic due times instead of sweep-then-sleep. One shared SNU queue serves search, manual refresh, details, and watches; coalesce identical in-flight/cache requests. Preserve a two-second minimum gap between successive SNU requests and one in-flight SNU request initially. Reserve fairness for due watches so interactive search cannot starve them; cancel obsolete searches.

Initial desktop profiles: Normal target 60 seconds, Faster target 30 seconds, Low activity target 120 seconds, with small bounded jitter. These are proposed app settings, not SNU-approved rates. Existing users importing 30 seconds retain that preference after source-policy validation. Do not offer 10 seconds as an approved or guaranteed option.

Show `last successful check`, actual age, `next check estimated`, and queue/cooldown reason. Do not claim 30-second checks when pagination, network time, and the request budget make that impossible. Target timing begins from the preceding attempt start; overruns schedule once as soon as budget permits, without accumulating missed runs.

Respect integer and HTTP-date `Retry-After`. HTTP 429 and explicit source maintenance/gates apply a source-wide cooldown; 403/access denial pauses automatic requests with an actionable message. Network failures use bounded backoff; parse errors get their own diagnostic state and retry policy. Notification delivery failure does not slow course polling. Per-section not-found/errors do not globally block unrelated valid work.

On sleep, network loss, or app exit, suspend work. On wake/recovery, expire watches first and stagger due work, with no catch-up burst. Manual refresh cannot bypass rate limits. During a declared site maintenance window, show the window and defer work. Do not use another endpoint or IP to evade a restriction.

For public distribution, assess combined load across installations. Per-device pacing alone is insufficient at scale. Seek an approved rate/data feed or institution guidance before promising large-scale low-latency monitoring. A future shared metadata/feed service requires a separate decision and operating plan; it is not silently introduced into v1.

### Opening events

For valid observations, apparent free seats = max(0, displayed quota - displayed registered count). This is not a prediction of personal eligibility. Preserve reserved/other-quota labels and restrictions.

Default behavior:

| Sequence | Event |
| --- | --- |
| First check already open | One initial-availability event if that preference is enabled; default on |
| 120/120 to 119/120 | Opening event: 1 apparent seat |
| 119/120 to 116/120 | Additional opening event: 4 apparent seats, increase of 3 |
| 116/120 to 116/120 | No new event |
| 116/120 to 117/120 | No new event |
| 117/120 to 116/120 | New observed increase; alert again |
| Same enrollment, larger quota | Event if derived free seats increase |
| Unknown/maintenance/malformed response | No opening event; keep last good baseline and update health |

After an outage, compare a fresh valid count with the last valid baseline; label the observation time/gap honestly. Do not imply the seat opened at the instant of the alert. Magnitude alone never suppresses an opening. Revalidation is required only when source identity, quota label/meaning, or required count fields are inconsistent. Record the inconsistency; mark that attempt Parse changed and withhold a new baseline/event until the adapter verifies the same identity and quota meaning. A valid large quota increase follows the ordinary alert rule.

Pause/resume/expiry are subscription lifecycle events. Resume first refreshes and establishes a subscription-local baseline, with an optional initial-open notice. New/resumed subscription baselines never reset the shared section observation baseline or suppress events for subscriptions already active. Record subscription activation time/event watermark to separate initial notices from ongoing shared section events. Check the current subscription revision before delivery so removed, paused, or expired watches cannot emit delayed alerts. Pending delivery must be revalidated or updated after a later observation; do not announce stale free seats after the section is known full. One section event can belong to multiple subscriptions but produces one desktop popup identifying relevant groups.

### Freshness and outages

Maintain last attempt and last success separately. Proposed stale threshold: max(2 times the requested target interval, 120 seconds). Independently show an error/limited-state badge immediately when a failed attempt is known; do not wait for staleness. Offline, rate limited, maintenance, source-limited, parse changed, and section not found have different messages. A known HTTP 200 maintenance page is an error, not a successful empty result.

Source health and per-section health are distinct. Group coverage uses successfully refreshed members, their ages, and current failure states; it does not erase member-level uncertainty. UI and diagnostics derive health from persisted attempts, not process existence or an empty log.

### Native notification delivery

Use the app's own stable identity and the Windows App SDK `AppNotificationManager`, not a PowerShell subprocess or another app's identity. Register the notification handler, call `Register()`, then read activation arguments in the documented order [S3]. Run the app unelevated; diagnose administrator launch explicitly rather than silently claiming notification readiness.

Popup content: title/course code/section/term, apparent seats and count, observed time, short no-reservation wording. Body click and Open registration open the official registration destination; View section opens the app. Include a copy-identifier action in the course panel. Do not promise SNU preselects a section until verified. Safe fallback opens the official homepage and makes the exact identifier easy to copy. Opening a browser never submits registration.

Persist an event before attempting delivery. Track Pending, OS accepted, Failed, Suppressed/Disabled when detectable, Superseded, and Activated per channel. A successful file/log write cannot acknowledge desktop delivery. Windows API success does not prove the user saw the banner. Keep in-app history and a first-run visible test. DND/system settings remain under user control; do not forcibly change them.

Clicking an existing notification after process exit must handle activation without creating duplicate polling. If the previous session explicitly quit, an action-only activation opens the destination and exits; normal user launch resumes active watches. Clicking an old alert indicates its observation age and does not imply that the opening still exists. Ordinary exit calls `Unregister()` to release the process handler; uninstall calls `UnregisterAll()` before removing binaries to clean up registration. These have different purposes [S13].

## 9. GitHub and Windows release contract

Primary asset: one `Setup.exe` from GitHub Releases, install, launch, notification test. The student needs no Python, Node, SDK, terminal, or Codex installation. GitHub Releases is the user-facing download channel, not expiring CI artifacts [S10].

The proposed deployment profile is an unpackaged WPF application with self-contained .NET and app-local Windows App SDK dependencies, including native dependencies. Plan `WindowsPackageType=None`, `SelfContained=true`, `WindowsAppSDKSelfContained=true`, and installed folder deployment. Freeze supported properties/toolchain during the installer spike. Microsoft documents installation of self-contained dependencies beside the EXE [S11]. However, that deployment guide says local `AppNotificationManager` does not require Singleton while the 1.8 API reference still states a dependency [S13]. Treat operation without separately installed Windows App Runtime packages as a hypothesis until M2 passes on a clean VM.

Package the whole publish directory in Inno Setup, not just its main EXE. Keep a stable Inno `AppId`, executable name, per-user x64 install path, and start-menu shortcut. Support a rerunnable installer, upgrade, and uninstall; a custom repair mode is not promised. Do not launch the app elevated from setup; if setup elevation is ever needed, relaunch in the original user's unelevated context or ask the student to launch the installed shortcut. No `PublishSingleFile` dependency for v1. An installer is a single download while installed DLLs remain ordinary implementation files. Inno supports single-EXE and non-admin installs [S12].

Gate on a clean Windows 11 x64 VM without developer PowerShell, .NET SDK, or preinstalled Windows App Runtime packages; inventory packages before and after install. Test running, tray, exited, and post-reboot notification activation plus single-instance forwarding. If the selected stable SDK cannot satisfy the app-local profile, stop M3 for an explicit deployment redecision with recorded evidence. The defined alternative is the same C#/WPF app with a signed setup that carries Microsoft's documented runtime prerequisites; test it in a separately named profile and update the ADR/contract before proceeding. Never silently mix deployment profiles or hand students manual dependency commands.

CI: restore pinned packages; format/analyze; run offline unit/integration tests and coverage; build/publish; scan dependencies/secrets; generate MIT and third-party license notices, SBOM, and SHA-256 checksums; produce a candidate installer. Windows desktop/installation tests use an interactive VM, not an assumption that a headless CI runner displays banners.

Release workflow: reviewed version/tag, successful gates, signed application/installer with timestamp, draft GitHub release, review release notes and smoke-test evidence, then publish only on owner authorization. Use scoped workflow permissions and pin external actions. No credentials, personal watch configuration, databases, logs, or private documents in repository/artifacts. Code signing and GitHub provenance/attestations have different purposes; neither promises the absence of SmartScreen prompts.

V1 update flow checks for a new release at most daily when enabled, displays release notes, and opens the trusted GitHub release for a user-initiated installer upgrade. Automatic background updater is deferred. Back up DB before migration, stop the app including its tray process gracefully before replacing binaries, and preserve local watches and preferences. Uninstall invokes notification cleanup in the correct user context before removing binaries and verifies no stale COM/notification activation; data removal is an explicit choice.

## 10. Verification contract

Use test-first development for domain and integration behaviors; target at least 80% line and branch coverage of Core/Application/adapter logic, with critical lifecycle branches explicitly covered. UI markup and generated code do not inflate this number. Every milestone includes appropriate tests and documentation rather than leaving both until packaging.

| ID | Required scenario and result |
| --- | --- |
| T01 | Regular, summer, winter and composite source terms resolve independently; selected or legacy-migrated term never becomes current term silently |
| T02 | Code punctuation/leading zeros survive search, manual entry, legacy migration, watch, DB, restart and alert |
| T03 | Saved section only on page 2 is found; later-page failures leave missing sections unknown and group coverage partial |
| T04 | Code/title/instructor/department filters and Korean/English names map correctly; unsupported full-text claims absent |
| T05 | 120/120,119/120,116/120,116/120,117/120 yields exactly two increase events after a full baseline |
| T06 | Quota increase (including a large valid one), unknown gap, initial-open preference, pause/resume, and same-name different courses follow the contract; inconsistent quota labels instead produce Parse changed |
| T07 | Log succeeds while desktop fails: desktop remains failed/retryable; persisted events survive restart |
| T08 | Failed alert followed by fewer seats retries with current valid content; later full/expired section supersedes stale delivery |
| T09 | 429 with both Retry-After formats, 403, 500, HTML maintenance, broken JSON/markup, missing row and offline produce no false openings |
| T10 | Last-success timestamp remains unchanged on failures while attempt/health changes; empty logs alone never mark healthy |
| T11 | Manual search/refresh obey shared pacing; grouped/individual duplicate targets issue one shared request set |
| T12 | Large group exceeds target interval: displayed estimates reflect queue delay; no overlapping runs or unbounded work |
| T13 | Pause/remove/expire during an in-flight request prevents delivery to that subscription; resuming a group sharing a section with an active individual watch never resets the active watch's baseline or suppresses its event |
| T14 | Midnight expiry, timezone changes, sleep/wake, restart and sign-in work without catch-up bursts |
| T15 | Second application launch focuses first instance; tray close continues, Quit stops; notification activation does not duplicate polling |
| T16 | Native installed banner/sound/click works in foreground/tray/exited state; permission disabled/DND shows honest diagnostics/history |
| T17 | Browser action opens approved HTTPS SNU/Genie only; malicious source text or URL never executes commands or becomes trusted navigation |
| T18 | Clean Windows account, Unicode/spaced path, no developer dependencies: inventory runtime packages before/after install; verify unelevated launch, no unplanned runtime install, reboot/cold activation, upgrade with tray shutdown, stable identity/path, and uninstall with no stale COM/notification registration |
| T19 | Legacy migration idempotent, read-only to originals; transaction/migration interruption is recoverable |
| T20 | Keyboard-only flow, screen reader labels, high contrast, 100/150/200% scaling, Korean/English text do not hide core controls |

Performance targets to measure on a documented reference PC: first usable window within 3 seconds, visible response to local actions within 100 ms, no main-thread network stalls, and less than 1% sustained idle CPU excluding active checks. These are proposed acceptance budgets, not current benchmark claims. Record actual memory/download-size measurements before setting a public promise.

## 11. Ordered implementation milestones

M0 is complete: the owner selected DuckFard/SNU-watcher and MIT, removed PDF import, and authorized development according to this plan on 2026-10-28. M1 source discovery and M2 native feasibility can run in parallel. Both exit gates must pass before M3; native installation/notification feasibility is a stack-defining gate, not a late packaging check. M3 then precedes M4; UI and alerts can progress against stable contracts with assigned file ownership. Review at each exit gate; do not hide a failed gate by proceeding to visual polish. See `docs/PROGRESS.md` for measured results and open gates.

| Milestone | Work and concrete deliverables | Exit gate |
| --- | --- | --- |
| M0 Confirm contract and repository | Repository/public visibility and MIT confirmed; revised plan and sanitized legacy reference saved; development authorized | Complete; public binary release and signing remain separately gated |
| M1 Source contracts | Bounded SNU form/search/detail/term tests; canonical ID and alias rules; sanitized fixtures; link/Genie fallback matrix | T01-T04; all unsupported fields documented; source behavior or fallback explicitly established |
| M2 Native install and notification spike | Minimal WPF shell, stable identity, single instance, handler/Register/activation ordering, local native banner, self-contained publish and draft installer | T15-T16/T18 smoke on clean VM with runtime inventory; visible notification and foreground/tray/exited/post-reboot clicks confirmed; no elevated launch or undeclared runtime install. Failed profile triggers deployment redecision before M3 |
| M3 Domain and persistence | Immutable models, watch/group/expiry state, opening policy, SQLite schema/migrations/outbox, legacy migration | T02/T05-T08/T13-T14/T19 pass offline; crash/retry semantics recorded |
| M4 SNU adapter and scheduler | Search/detail parser, metadata caching, query coalescing, pacing/backoff, health attempts, notifications-independent poll loop | T03-T04/T09-T12; fake clock plus small opt-in live verification; no full-catalog or per-keystroke requests |
| M5 Complete student UI | Onboarding, search/filter rows, manual code/section lookup, watch controls/groups, expiry, freshness, source-status panel, tray/preferences/localization | Full search-to-watch-to-pause/resume/expire journey; T13-T15/T20; component screenshots and layout review |
| M6 Alert integration and diagnostics | Wire durable events to native channel; browser actions, delivery history/retries, visible test, logs and diagnostics export | T05-T10/T16-T17; a simulated opening from official-shaped fixtures triggers correct installed popup and destination |
| M7 Release candidate | Harden upgrade/uninstall, retention, settings, MIT/third-party notices/SBOM, CI artifacts/signing, documentation; controlled student pilot | All T01-T20, no critical/high defects, defined coverage gate, source/load policy reviewed, clean VM install and upgrade evidence |
| M8 Publish and maintain | Owner authorizes exact release; publish versioned installer/checksum/notes; monitor support reports and adapter compatibility | Download/install from GitHub tested by a fresh user; repeatable release runbook and rollback path |

Use a fixture-driven replay mode for development/E2E, separate from production source selection. It must show a clear demo badge and never present synthetic availability as live data. Live checks are opt-in and small; CI must not continuously crawl SNU.

## 12. Documentation while coding

Maintain one coherent documentation set in DuckFard/SNU-watcher:

- This product contract, updated for accepted decisions; requirement IDs link to tests and milestones.
- `docs/architecture.md`: module ownership, lifecycle, persistence, event flow.
- `docs/decisions/`: ADRs for stack, canonical identity, rate policy, grouping, notification activation, installer, and updates.
- `docs/integrations/snu.md`: exact observed fields/endpoints, fixture date, pagination and failure examples, term semantics, what is internal/unsupported.
- `docs/migration.md`: legacy TOML/SQLite migration, term confirmation, idempotency, backup and rollback; this is not document/PDF import.
- `docs/testing.md` and `docs/releases.md`: local checks, clean-VM/manual notification protocol, release signing, upgrade/rollback.
- `README.md`: student download/start guide; contributor setup separate from student instructions. Include actual alert rules and refresh semantics.
- `SECURITY.md`, privacy statement and third-party notices before public release; the root MIT `LICENSE` is established during planning.

Public interfaces get XML documentation describing inputs, outputs, cancellation, errors and invariants. Complex parsers/scheduler decisions get short comments explaining assumptions and why; avoid narrating obvious code. Schema changes include migration documentation and fixtures. Every feature PR includes relevant documentation changes and test evidence. No duplicate personal notes or raw student data in project docs.

## 13. Open questions and handoff

### Owner choices

Confirmed: public repository [DuckFard/SNU-watcher](https://github.com/DuckFard/SNU-watcher), MIT license, no PDF import, and authorization to develop according to this plan. Development source/candidates may be saved to the repository; a signed public binary release remains gated by M7/M8.

1. Default development identity: display name `SNU Watcher`, publisher `DuckFard`, internal identity `DuckFard.SnuWatcher`. Freeze signing/installer identity before release.
2. Proceed with Windows 11 x64 and Korean/English launch scope under the accepted plan; macOS is outside v1.
3. Signing budget/publisher certificate for release. MIT applies to original project code/docs; dependencies and external SNU assets retain their own terms.

These choices can be reviewed with this concrete plan. No additional plugin is required for planning. Use the existing GitHub connector or Git for the confirmed repository, within the owner's granted scope. Saving source/planning progress does not authorize an installer release.

### Bounded evidence needed in M1

- Seasonal term identity/alias examples and complete filter/detail fixtures.
- Verified official course/registration links and Genie fallback behavior. Deep links that require unsupported session state remain disabled.
- Source operator guidance on automated access/rate/distribution, or an approved feed if available. Absence of 429 responses does not prove an approved rate. Document unresolved guidance before a public pilot.

### Useful working setup

When building is authorized, attach DuckFard/SNU-watcher as a Codex project, keep this plan and ADRs in it, and implement milestone-sized branches/PRs. Use isolated worktrees for independent UI/domain work after contracts stabilize. Use the existing agents for code/security review. A clean interactive Windows VM/account is more useful than another cloud plugin for the release gates. Figma is optional if the owner wants a separate visual design review; it is not needed to implement WPF.

Handoff: development is authorized. Complete M1/M2 evidence first; do not mark clean-machine native validation passed from a developer PC with preinstalled runtimes. Continue into M3 only after the exit gates pass, preserving the old watcher's runtime throughout.

## 14. Sources and reference projects

Sources support factual capabilities and constraints; chosen defaults, budgets and architecture are proposals. Source observations may change and must be revalidated at M1.

- S1: [Microsoft WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) and [supported .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
- S2: [Microsoft Windows notification API overview](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/).
- S3: [Use app notifications in a WPF/.NET app](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet).
- S4: [Tauri notification plugin](https://v2.tauri.app/plugin/notification/).
- S5: [SNU public search/notice page](https://sugang.snu.ac.kr/sugang/co/co100.action); observed form actions `cc100InterfaceSrch.action` and `cc101ajax.action` under `/sugang/cc/` are internal site interfaces, not published API guarantees.
- S6: [SNU registration misconduct notice 6497](https://sugang.snu.ac.kr/sugang/co/co102.action?boardId=NOTICE&noticeInptOrd=6497&workType=IQ&searchType=1&searchKeyword=&page=1).
- S7: [SNU winter load/maintenance notice 6551](https://sugang.snu.ac.kr/sugang/co/co102.action?boardId=NOTICE&noticeInptOrd=6551&workType=IQ&searchType=1&searchKeyword=&page=1) and [official registration manual](https://sugang.snu.ac.kr/kor/guide/manual.pdf).
- S9: [SNU IT services and Genie description](https://www.snu.ac.kr/campuslife/aid/it), [official Genie service notice](https://biosci.snu.ac.kr/board/notice?bbsidx=21149&bm=v), and [Genie](https://snugenie.snu.ac.kr/).
- S10: [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases).
- S11: [Microsoft Windows App SDK self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps). Use the self-contained profile; framework-dependent deployment is a distinct configuration with different prerequisite handling.
- S12: [Inno Setup official features](https://jrsoftware.org/isinfo.php).
- S13: [AppNotificationManager.Unregister API reference](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.unregister?view=windows-app-sdk-1.8) and [UnregisterAll API reference](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.unregisterall?view=windows-app-sdk-1.8). These references retain a Singleton-dependency note differing from S11; M2 must establish behavior for the pinned SDK.
- [Wafflestudio SNUTT](https://github.com/wafflestudio/snutt): SNU catalog/timetable modeling and UX reference, with maintainers' load/crawling cautions. It is not proof of permission or an approved polling rate.
- [Wafflestudio snutt-proxy](https://github.com/wafflestudio/snutt-proxy): reference for restricted syllabus routes, cookie isolation, and link compatibility pitfalls; do not silently depend on its public service.
- [Historical Snu-sugang app](https://github.com/kgwangrae/Snu-sugang): historical UI reference only; not a current endpoint or reliability foundation. Existing auto-registration scripts are unsuitable foundations for this notification product.

No third-party code is copied by this plan. Before reusing code/assets later, check the actual file's license and preserve required attribution. No permission to copy SNU branding or website assets is assumed.
