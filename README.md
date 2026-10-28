# SNU Watcher

A planned Windows desktop companion for Seoul National University course availability alerts.

**Status: planning.** The desktop UI and downloadable installer have not been built. This repository preserves the implementation plan and the existing Python CLI prototype for reference. Saving this progress does not start desktop implementation.

## Plan

Read [the desktop implementation plan](docs/DESKTOP_APP_PLAN.md) for requirements, architecture, source integration, monitoring rules, tests, milestones, and GitHub release gates.

The proposed stack is C# / .NET 10, WPF, SQLite, and native Windows App SDK notifications, distributed through GitHub Releases as a Windows installer EXE.

Planned features include:

- Find SNU courses by code, name, instructor, department, or keyword.
- View sections, schedules, locations, enrollment, capacity, and source restrictions.
- Watch exact sections or confirmed groups of sections.
- Receive a popup when apparent free seats increase, with an action opening official SNU registration in the default browser.
- Manage pause, resume, removal, expiry, freshness, outages, and alert history.
- Open SNU Genie in the browser for additional information.

PDF import and OCR have been removed from scope. Students will add sections through course search or exact identifiers. Legacy watcher migration remains a separate planned Settings action.

An alert does not reserve a seat or guarantee eligibility. Registration remains manual on the official SNU site. This is an unofficial companion, not an SNU service.

## Existing prototype

The [legacy Python watcher](legacy/python-watcher/) contains the previously developed CLI and offline tests. Its known limitations are documented there and in the plan; it is not the planned desktop application or a ready-made installer.

Personal watch configuration, course-state databases, logs, virtual environments, and private documents are excluded from this repository. Example configuration and sanitized fixtures are included.

## License

Original project code and documentation are licensed under the [MIT License](LICENSE), copyright 2026 DuckFard. Third-party dependencies, SNU data, branding, and other external content retain their respective terms.
