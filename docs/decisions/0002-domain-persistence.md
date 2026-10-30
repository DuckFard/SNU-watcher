# Domain and persistence execution contract

Status: design prepared on 2026-10-29; implementation waits for the M2 exit gate. This refines M3 of the [accepted plan](../DESKTOP_APP_PLAN.md), without starting the watch engine early.

## Identity and ownership

Core owns a validated six-part section key: academic year, semester, detail semester, course code, three-digit section and subsubject. Infrastructure maps its source DTOs to that key. Core does not reference Infrastructure or infer aliases.

The per-user database needs one monitoring owner across Windows sessions. Keep session-local window forwarding, and add a separate per-user database-owner gate before opening the store. A second session must receive a clear diagnostic rather than start another scheduler. Any Windows mutex must acquire/release on its owning thread, as the existing coordinator does.

## Immutable policy and subscription state

Shared section observations and each subscription member's initialization are separate. Member initialization is keyed by subscription, member and activation generation; partial group refresh initializes only successful members. Resume/new membership establishes a new activation watermark without resetting shared baselines or other subscriptions.

Only a newly issued attempt after activation can initialize that generation. Persist attempt order and optimistic versions; wall-clock timestamps alone cannot order responses after clock corrections. A new/resumed watch with initial notices off skips its first qualifying observation even if that observation generates an increase for an older watch.

One accepted observation creates at most one section event/outbox item. Recipient associations retain whether the reason was initial availability or increased seats. Overlapping individual/group subscriptions therefore share one popup. Unknown data preserves valid baselines and last-success information. Known bilingual labels map to one verified quota meaning; inconsistent semantics require source revalidation.

Expiry includes the selected local day. Persist the original date/zone and exclusive next-day boundary resolved to UTC, defaulting to Asia/Seoul. Later host timezone changes do not shift an existing expiry. Test synthetic DST gaps/overlaps even though Seoul currently has none.

## Transactions and delivery

An observation commit validates expected versions and atomically records the attempt/observation, valid baseline, eligible member watermarks, event, recipients and outbox. Replayed attempts are idempotent. Network and notification calls occur outside database transactions.

Before delivery, reload current generation, status, expiry and counts. Known-full supersedes pending delivery; fewer remaining seats require current content. Older pending events can be superseded by newer section events while retaining their audit records. Stale pending delivery waits for a fresh check. Keep trigger and latest-observation times distinct.

Lifecycle changes and sending have an explicit linearization gate: after pause/remove completes, a send for that generation cannot begin. An unlocked revision check immediately followed by an OS call is insufficient. A rename/expiry edit changes definition revision without automatically resetting initialization or discarding an otherwise eligible pending event.

Delivery is at least once with a stable native deduplication key. Crash after commit preserves pending work; crash after OS acceptance can retry the same key. Direct browser notification clicks have no app receipt and never prove registration.

## Storage and migration boundaries

Use parameterized SQL, foreign keys per connection, private cache, bounded busy timeout, explicit durability and short transactions. Database operations belong on a bounded background lane: Microsoft's [SQLite async methods execute synchronously](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).

Verify the actual native `sqlite_version()` in integration/installed tests. Before enabling WAL, select a native package containing the [WAL-reset corruption fix](https://www.sqlite.org/wal.html), such as 3.51.3 or the documented 3.50.7/3.44.6 backports. A managed NuGet version alone is not proof. Pin the supported package and verify it at implementation time.

Legacy migration is explicit, bounded and read-only at the source. Do not instantiate the old repository, whose constructor writes schema. Resolve short terms against confirmed full identities. Import old counts only as stale provenance, never as current baselines or delivery acknowledgement. Use a consistent live SQLite read transaction; do not copy only a live database file or declare an actively written database immutable. An import ledger uses legacy identity plus confirmed target identity, not filename or changing enrollment counts.

## Execution split and exit evidence

Freeze Core value objects and application persistence ports first. Assign separate ownership for Core/policy tests, Infrastructure SQLite/store tests, Application use-case tests and legacy-reader/import tests. Root owns project/solution wiring, dependency locks and CI coverage/audit expansion.

Required RED/GREEN cases include initial-open on/off, every valid seat increase, quota increases, over-capacity/zero capacity, unknown gaps, partial groups, resume overlap, per-generation eligibility races, parameterized malicious labels, atomic rollback at each transaction stage, replay/concurrent commits, outbox crash recovery, inclusive expiry, clock/timezone changes, interrupted schema migration and read-only live legacy import. Retention must not remove required valid baselines/member state. Both new assemblies receive individual coverage gates; CI must derive its audit project set from the solution instead of assuming the current five projects.
