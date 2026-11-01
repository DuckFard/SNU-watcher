# Foundation architecture

This document describes the first implementation slice of the [desktop plan](DESKTOP_APP_PLAN.md). Source contracts and the native Windows host are deliberately independent so catalog parsing can be tested without initializing Windows notification APIs. The full watch engine is a later gated milestone.

## Implemented module responsibilities

- `SnuWatcher.Infrastructure`: anonymous SNU HTTP source, response contracts and source data transfer objects. HTML/JSON from SNU is untrusted input. Complete, partial and failed results must remain distinguishable. It does not collect SNU credentials or submit registrations.
- `SnuWatcher.Core`: independent, immutable section identities, seat/quota models, opening and recipient policies, and inclusive local-date expiry. It has no network, persistence or Windows dependencies. Desktop does not yet reference it; [ADR 0003](decisions/0003-external-contribution-review.md) records the reviewed contribution and corrections.
- `SnuWatcher.Windows`: validated activation requests and single-instance coordination. OS launch parameters are untrusted input; only supported actions and known destinations are accepted.
- `SnuWatcher.Desktop`: WPF composition, the native notification API boundary, tray/window lifecycle and the feasibility interface. The first interface is a validation shell, not the completed course-search/watchlist experience.
- Separate xUnit projects validate Core policies, source behavior and Windows helper behavior. Direct native display and browser protocol clicks are covered by the interactive acceptance protocol rather than assumed from a mock.

The solution and shared build properties pin .NET, enable nullable analysis, and preserve locked dependency graphs. CI has read-only repository permissions and runs offline relative to SNU. It does not send notifications or publish a public release.

## Reserved for later milestones

Durable alert/subscription state, groups, expiry application, SQLite migrations, a notification outbox, actual polling, the full student UI and legacy migration remain gated by M1/M2. The external Application patch is held for revision; its storage and scheduling contracts are not adopted. The existing Python reducer/tests remain a behavioral reference, not the desktop host's production engine. PDF import is excluded.

No network, SQL, or source parsing belongs in a WPF event handler. The native shell may invoke its small notification/link adapters for feasibility; future ViewModels call application services once those milestones begin.

## External boundaries

SNU course data uses a replaceable internal-site adapter. Registration and Genie open in the student's normal browser; no embedded authenticated session is created. Anonymous transport-session state stays in memory. Never confuse a successful HTTP request with a successfully parsed exact section, or OS notification acceptance with a student seeing a banner.

The Windows shell runs unelevated and owns a stable installed executable path. Registration notification body/button clicks open the fixed official HTTPS URL through Windows directly; they do not start the app or provide it a click receipt. Ordinary application launches route to the existing owner. Ordinary exit releases live handlers; uninstall removes persistent notification registration before removing the executable. A second instance cannot become a second polling owner when the engine is added. The retained COM callback path is best-effort; its observed failure and the protocol redecision are recorded in ADR 0001.

## Validation boundary

This developer PC already contains Windows App Runtime packages. Local native success can establish developer-machine behavior only. The self-contained deployment decision remains provisional until the documented clean-machine gate passes. Do not remove shared OS/runtime packages to manufacture that evidence.
