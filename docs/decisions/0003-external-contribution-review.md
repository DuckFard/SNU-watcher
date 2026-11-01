# Review of external Core and Application contributions

Date: 2026-11-01, Asia/Seoul.

Status: adopt corrected Core library; hold the external Application engine for revision. M2 was open during this contribution review and subsequently passed [native acceptance](../native-validation.md) on 2026-11-01. The [domain/persistence contract](0002-domain-persistence.md) and [accepted plan](../DESKTOP_APP_PLAN.md) remain the acceptance criteria.

## Supplied artifacts and decision

The owner supplied two patches attributed to an external AI session:

- Core: `f4e9ccc34e3758d4ae963c11a242ddd484a1de10`.
- Application engine: `b63aaf509d873564fa17547bb64e80f4bea70419`.

The supplied patch files have SHA-256 `8D1B392D4F01960CA5827CC8692960828B0E3E23F7BD578B661CB2B4E321A6B2` (Core) and `823B0D1CC698B0B0D00FEFC15FE2881A09655CC43115ED13AC03CB4A25F51DC2` (Application). These identify the received artifacts; their commit IDs do not imply that the external branches were pushed to this repository.

Both apply cleanly in an isolated copy of the development checkout. Adopt the six-part section identity, seat/quota policy, opening policy, recipient resolution and expiry library, with the corrections below. It has no Desktop reference and does not enable monitoring. Retain the engine's useful fake-clock and lifecycle scenarios as design evidence, but do not adopt its production implementation or persistence port yet. The reported unfinished SQLite draft was not supplied; no SQLite dependency or implementation is accepted from that report.

## Executed evidence

The pinned .NET SDK 10.0.403 performs a real locked restore and Release build of the combined patches. Real xUnit execution passes the original 285 cases: 64 Core, 50 Application, 55 source and 116 Windows. The build emits seven xUnit analyzer warnings, contrary to the report's warning-free claim. Custom-shim and mutation-test claims remain attributed to the report and are not substituted for this evidence.

Core review adds and reproduces these failures before correcting them:

- A synthetic DST gap ends at 00:59:30 on the following local date. Advancing invalid midnight by whole minutes expires the watch 30 seconds late. Find the exact first valid instant while retaining the earlier occurrence for ambiguous midnight.
- Notification and initialization collections retain mutable input lists and expose mutable result lists. Snapshot constructor and record-update inputs and return read-only collections.

The corrected review copy passes 68 Core cases with 96.10% line and 97.27% branch coverage. The Core analyzer warning is corrected. After selective integration, the main checkout passes locked restore, a zero-warning/error Release build and all 239 tests (68 Core, 55 source, 116 Windows), with independent assembly line/branch gates above 80%. The seven-project transitive dependency audit reports no known vulnerable packages. CI for the resulting commit is separate evidence; none of these checks establishes native acceptance.

## Application blockers

Four new xUnit regressions execute and fail against the supplied engine:

| Scenario | Observed result | Required behavior |
| --- | --- | --- |
| Pending one-seat alert fails; three minutes pass; the source becomes full and delivery recovers | The old alert sends before the fresh full response is read | Stale pending delivery waits for fresh counts; known-full supersedes it |
| The same observation commit is replayed | A second GUID and outbox entry are created | Identical replay is idempotent; conflicting replay is rejected |
| Request spacing is set to one second | Options validation accepts it | Enforce the plan's two-second minimum |
| A returned watch's writable section list is cleared | Stored membership changes without a lifecycle operation | Expose immutable snapshots and preserve owned membership |

Additional source-review findings require regressions and redesign before adoption:

- Notification delivery is awaited in the polling path while holding the lifecycle semaphore. A stalled notifier can block later course checks and watch controls. Use a bounded delivery lane while preserving pause/remove and send linearization.
- The persistence commit lacks complete attempt/outcome data and expected versions; health is written separately. Define an atomic observation, health, baseline, initialization, event, recipient and outbox commit, with stale/replay rejection and crash recovery.
- Query scheduling uses response-completion wall time. Verify target timing from attempt start and use monotonic scheduling so latency and wall-clock corrections do not silently extend the interval.

## Follow-up boundary

M2 native acceptance was completed independently after this review. Before integrating a watch engine, cover the four reproduced failures and the additional concurrency/timing findings, then verify the remaining ADR 0002 cases with real tests and individual assembly coverage gates. SQLite must additionally prove its actual native engine version/source ID, safe WAL version, transactional rollback, migrations, reopen/crash behavior and read-only live legacy import. This contribution review alone does not establish completion of those milestones.
