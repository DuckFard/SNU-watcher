# SNU Watcher

**Watch the courses you want. Hear when a seat opens.**

SNU Watcher is a Windows desktop app being built for Seoul National University students who are waiting for a place in a full class. It is designed to check the sections you choose, alert you when a seat becomes available, and take you to the official SNU registration page so you can try to register.

**Currently in development:** the desktop build is an installation and notification test app. Course search, watchlists, and automatic availability checks are still being developed. A public desktop release is not available yet. Follow the [development progress](docs/PROGRESS.md) for the current status.

## Planned features

| Feature | What it will do |
| --- | --- |
| Find courses | Search by course code, name, department, instructor, or keyword using the SNU course catalog. |
| Compare sections | View instructors, schedules, locations, enrollment, capacity, and available seats. |
| Build a watchlist | Watch one exact section or group several sections under a course, such as any selected section of College Writing. |
| Get seat alerts | Receive a Windows popup identifying the course and section, with a shortcut to official SNU registration. |
| Manage your watches | Add, pause, resume, or remove watches, and choose when they expire. |
| Check data freshness | See the last successful check and clear status messages when information is delayed or unavailable. |
| Explore further | Open SNU Genie in your browser for additional course information. |

## How it will work

1. Select the academic term and find the course you want.
2. Add a section, or a group of acceptable sections, to your watchlist.
3. Keep SNU Watcher running while it checks for availability changes.
4. When a seat opens, click the notification to open the [official SNU registration site](https://sugang.snu.ac.kr/sugang/co/co010.action) and register yourself.

**An alert does not reserve a seat.** Another student may register first, and registration restrictions still apply. SNU Watcher will leave registration to you on the official site.

SNU Watcher is an independent project and is not affiliated with or endorsed by Seoul National University.

## Downloads and platform

The first desktop release targets **Windows 11 x64**, with an installer `.exe` distributed through [GitHub Releases](https://github.com/DuckFard/SNU-watcher/releases). The installer is intended to include its required runtimes so students can install the app without setting up development tools.

For now, development installers are unsigned validation builds. The desktop's installation, notification, and lifecycle checks are still in progress; these builds do not monitor courses.

The earlier command-line prototype is preserved in [legacy/python-watcher](legacy/python-watcher/), along with its setup instructions and known limitations.

## Develop locally

The desktop uses **C# / .NET 10 and WPF**, with native Windows notifications. SQLite is planned for storing watches and alert history locally.

Desktop development is on the `development/windows-foundation` branch. On Windows 11 x64, install the .NET SDK version specified in [global.json](global.json), then run:

```powershell
git clone --branch development/windows-foundation https://github.com/DuckFard/SNU-watcher.git
cd SNU-watcher
.\scripts\Test-Foundation.ps1
```

The script restores locked dependencies, builds the solution, and runs the offline foundation tests with an 80% line and branch coverage requirement. These tests do not log into SNU, submit registrations, or send Windows notifications.

See [native validation](docs/native-validation.md) for instructions to build the current test app and installer. Installer builds also require Inno Setup.

## Project documentation

- [Development progress](docs/PROGRESS.md) — implemented work, test evidence, and open milestones.
- [Implementation plan](docs/DESKTOP_APP_PLAN.md) — feature scope, monitoring behavior, and release requirements.
- [Architecture](docs/architecture.md) — application structure and design decisions.
- [SNU integration](docs/integrations/snu.md) — data sources, verified behavior, and limitations.
- [Testing](docs/testing.md) — automated checks and coverage requirements.

## License

Original project code and documentation are licensed under the [MIT License](LICENSE), copyright 2026 DuckFard. Third-party dependencies, SNU data, branding, and other external content retain their respective terms.
