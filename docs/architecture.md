# Foundation architecture

This document describes the implemented foundations of the [desktop plan](DESKTOP_APP_PLAN.md). Domain policies, persistence, source adapters and the native Windows host remain independently testable. The full watch engine and student interface are later milestones.

## Implemented module responsibilities

- `SnuWatcher.Infrastructure`: anonymous SNU HTTP source, response contracts and source data transfer objects, plus the SQLite connection factory, bounded database command lane and transactional watch store. HTML/JSON from SNU is untrusted input. Complete, partial and failed results remain distinguishable. It does not collect SNU credentials or submit registrations.
- `SnuWatcher.Core`: independent, immutable section identities, seat/quota models, opening and recipient policies, and inclusive local-date expiry. It has no network, persistence or Windows dependencies. Desktop does not yet reference it; [ADR 0003](decisions/0003-external-contribution-review.md) records the reviewed contribution and corrections.
- `SnuWatcher.Application`: immutable persistence commands/snapshots and a pure observation preparer. Latest completed check order and last valid counts are separate; current evaluation time controls expiry. Prepared effects carry section/catalog versions for atomic comparison and commit. There is no scheduler or notification delivery lane yet.
- `SnuWatcher.Windows`: validated activation requests, session-local UI forwarding, and a separate current-user-only global database-owner gate. The owner thread acquires/releases its mutex across asynchronous caller operations; global scope is independent of Windows session IDs. OS launch parameters are untrusted input; only supported actions and known destinations are accepted.
- `SnuWatcher.Desktop`: WPF composition, the native notification API boundary, tray/window lifecycle and the feasibility interface. The first interface is a validation shell, not the completed course-search/watchlist experience.
- Four xUnit projects validate Core policies, Application contracts/reduction, source/real SQLite behavior and Windows helpers. Direct native display and browser protocol clicks are covered by the interactive acceptance protocol rather than assumed from a mock.

The solution and shared build properties pin .NET, enable nullable analysis, and preserve locked dependency graphs. CI has read-only repository permissions and runs offline relative to SNU. It does not send notifications or publish a public release.

## Reserved for later milestones

M1/M2 acceptance is recorded in [development progress](PROGRESS.md). The first M3 slice implements durable subscription/member state, initial schema creation, atomic observation/event/outbox writes and replay-safe receipts. Legacy read-only import/ledger, remaining offline delivery decisions and production startup integration remain unfinished; M3 is not complete. Polling, notification delivery coordination, the full student UI and release retention are later work. The external Application patch's storage and scheduling contracts are not adopted. The existing Python reducer/tests remain a behavioral reference, not the desktop host's production engine. PDF import is excluded.

The store verifies the pinned native SQLite identity before opening a file, recognizes its schema before journal changes, and rechecks a fresh schema under the write reservation to avoid concurrent initialization races. Database operations execute on a bounded background lane with private-cache connections, foreign keys and explicit durability. Completion health, valid baseline, member initialization, event, recipients, outbox and receipt share one transaction. Identical replay returns its original event ID before version checks; conflicting replay fails. [ADR 0002](decisions/0002-domain-persistence.md) and the [dependency record](sqlite-dependencies.md) describe the remaining contract and runtime evidence.

No network, SQL, or source parsing belongs in a WPF event handler. The native shell may invoke its small notification/link adapters for feasibility; future ViewModels call application services once those milestones begin.

## External boundaries

SNU course data uses a replaceable internal-site adapter. Registration and Genie open in the student's normal browser; no embedded authenticated session is created. Anonymous transport-session state stays in memory. Never confuse a successful HTTP request with a successfully parsed exact section, or OS notification acceptance with a student seeing a banner.

The Windows shell runs unelevated and owns a stable installed executable path. Registration notification body/button clicks open the fixed official HTTPS URL through Windows directly; they do not start the app or provide it a click receipt. Ordinary application launches route to the existing owner. Ordinary exit releases live handlers; uninstall removes persistent notification registration before removing the executable. A second instance cannot become a second polling owner when the engine is added. The retained COM callback path is best-effort; its observed failure and the protocol redecision are recorded in ADR 0001.

## Validation boundary

This developer PC already contains Windows App Runtime packages. Local native success can establish developer-machine behavior only. The selected self-contained profile passed the documented clean-VM M2 gate on 2026-11-01; subsequent installer/runtime changes still need appropriate independent regression checks. Do not remove shared OS/runtime packages to manufacture that evidence.
