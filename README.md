# SNU Watcher

A planned Windows desktop companion for Seoul National University course availability alerts.

**Status: foundation development.** Development is authorized and the first source-contract and Windows-native feasibility projects are in progress. The complete course-search/watchlist application and a production installer are not finished. See [milestone progress and evidence](docs/PROGRESS.md).

## Plan

Read [the desktop implementation plan](docs/DESKTOP_APP_PLAN.md) for requirements, architecture, source integration, monitoring rules, tests, milestones, and GitHub release gates.

The selected stack is C# / .NET 10, WPF, SQLite, and native Windows App SDK notifications. The intended distribution is a Windows installer EXE through GitHub Releases. Development candidates are separate from signed public releases.

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

## Development

Use Windows 11 x64 and the .NET SDK pinned in `global.json`:

```powershell
dotnet restore SnuWatcher.slnx
./scripts/Test-Foundation.ps1
```

The first restore creates/validates pinned dependency graphs; the verification script then uses locked restore, builds the solution, and runs offline tests with line/branch coverage gates. No SNU login, registration, or native notification is performed by the test suites.

Read [architecture](docs/architecture.md), [testing](docs/testing.md), [SNU contracts](docs/integrations/snu.md), and [native validation](docs/native-validation.md) before changing integration or lifecycle behavior. The native shell is for installation/notification validation; it does not yet implement the planned monitoring engine. PDF import remains excluded.

## License

Original project code and documentation are licensed under the [MIT License](LICENSE), copyright 2026 DuckFard. Third-party dependencies, SNU data, branding, and other external content retain their respective terms.
