# Foundation architecture

This document describes the first implementation slice of the [desktop plan](DESKTOP_APP_PLAN.md). Source contracts and the native Windows host are deliberately independent so catalog parsing can be tested without initializing Windows notification APIs. The full watch engine is a later gated milestone.

## Implemented module responsibilities

- `SnuWatcher.Infrastructure`: anonymous SNU HTTP source, response contracts and source data transfer objects. HTML/JSON from SNU is untrusted input. Complete, partial and failed results must remain distinguishable. It does not collect SNU credentials or submit registrations.
- `SnuWatcher.Windows`: validated activation requests and single-instance coordination. OS launch parameters are untrusted input; only supported actions and known destinations are accepted.
- `SnuWatcher.Desktop`: WPF composition, the native notification API boundary, tray/window lifecycle and the feasibility interface. The first interface is a validation shell, not the completed course-search/watchlist experience.
- Separate xUnit projects validate source behavior and Windows helper behavior. Direct native display/COM activation is covered by the interactive acceptance protocol rather than assumed from a mock.

The solution and shared build properties pin .NET, enable nullable analysis, and preserve locked dependency graphs. CI has read-only repository permissions and runs offline relative to SNU. It does not send notifications or publish a public release.

## Reserved for later milestones

Core alert state, subscriptions/groups, expiry, SQLite migrations, durable notification outbox, actual polling, the full student UI and legacy migration must be implemented after M1/M2 pass. The existing Python reducer/tests remain a behavioral reference, not the desktop host's production engine. PDF import is excluded.

No network, SQL, or source parsing belongs in a WPF event handler. The native shell may invoke its small notification/link adapters for feasibility; future ViewModels call application services once those milestones begin.

## External boundaries

SNU course data uses a replaceable internal-site adapter. Registration and Genie open in the student's normal browser; no embedded authenticated session is created. Anonymous transport-session state stays in memory. Never confuse a successful HTTP request with a successfully parsed exact section, or OS notification acceptance with a student seeing a banner.

The Windows shell runs unelevated and owns a stable installed executable path. Notification clicks must route to the existing process when one exists. Ordinary exit releases live handlers; uninstall removes persistent notification registration before removing the executable. A second instance cannot become a second polling owner when the engine is added.

## Validation boundary

This developer PC already contains Windows App Runtime packages. Local native success can establish developer-machine behavior only. The self-contained deployment decision remains provisional until the documented clean-machine gate passes. Do not remove shared OS/runtime packages to manufacture that evidence.
