# Legacy Python watcher

This is the existing notification-only CLI prototype, preserved as a behavioral reference for the planned desktop application. Its source is unchanged in this snapshot. It has no graphical UI or packaged Windows installer.

The [desktop plan](../../docs/DESKTOP_APP_PLAN.md) describes the replacement architecture and release gates. This reference has known limitations: the polling interval is a delay after a whole sweep, runtime errors are not reliably surfaced, console success can conceal failed desktop delivery, and Windows notifications use an installed PowerShell identity without a registration click action. It must not be wrapped unchanged as the production desktop engine.

## Behavior to preserve

- Match academic year, term, course code, and leading-zero section exactly.
- Follow bounded, spaced result pages; share searches for sections of the same course.
- Alert when apparent free seats increase, including when the section was already open.
- Suppress new alerts for unchanged or decreasing free-seat counts after acknowledgement.
- Preserve the last valid baseline across unknown responses; retry unacknowledged alerts.
- Keep registration manual; no SNU credential collection or registration submissions.

## Optional local development

From this directory, with Python 3.12 or later:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e ".[dev]"
Copy-Item watcher.example.toml watcher.toml
.\.venv\Scripts\snu-slot-watcher.exe --config watcher.toml validate
```

The example course/term identifiers are historical examples; select and verify current identifiers before any live check. Your local `watcher.toml`, database, and logs are ignored by Git. No personal runtime configuration is included here.

The tests use sanitized HTML and fake services:

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
.\.venv\Scripts\python.exe -m coverage run --source=snu_slot_watcher --branch -m unittest discover -s tests
.\.venv\Scripts\python.exe -m coverage report
```

Tests are offline. A mocked notification test is not evidence that a Windows banner is visible. Windows installation, activation and notification reliability are explicit gates for the future desktop app.

Original project code is covered by the repository's [MIT License](../../LICENSE). External dependencies and SNU content retain their own terms.
