# Foundation verification

The Core, Application, source/storage and native Windows foundations use xUnit/VSTest with synthetic time zones, fake HTTP, temporary SQLite databases and real local IPC where appropriate. Tests never log into SNU, submit registrations, open the student's watch database, or send native notifications. The shared runsettings measure Core, Application, Infrastructure and Windows helper logic; WPF/Windows App SDK display and activation require the separate interactive gate.

Use the SDK pinned in `global.json` and the committed dependency lock files. Run the locked verification script:

```powershell
./scripts/Test-Foundation.ps1
```

If the SDK is installed outside PATH, pass its executable to `-Dotnet`. The script restores in locked mode, performs a Release build, runs all four offline suites, writes TRX and Cobertura reports under ignored `artifacts/test-results`, and requires at least 80% line and branch coverage for each suite's intended assembly. Dependency coverage cannot mask gaps in that assembly. `-SkipCoverageGate` is diagnostic only; it does not satisfy the milestone gate.

Keep async state machines in the measured logic. Excluding `CompilerGeneratedAttribute` removes async implementations as well as generated scaffolding; the shared settings exclude explicit generated code instead.

Dependency versions are pinned in project files and `packages.lock.json`. CI uses locked restore, commit-pinned GitHub actions, and no SNU requests. An audit listing is evidence for review, not an assertion that the command automatically fails on every advisory.

Persistence tests use the production connection factory and actual pinned native SQLite engine, not an in-memory repository substitute. They exercise replay, version conflicts, unknown-response ordering, generation eligibility, transactional rollback and reopening pending work. Injected failures run before transaction commit; a separate reopen verifies durable state. The bounded command-lane tests distinguish cancellation before execution from cancellation after a commit has begun. Native engine pins and notice files are recorded in [SQLite dependencies](sqlite-dependencies.md).

Windows database-owner tests use actual kernel mutex contention, release and abandonment with private test identities. Namespace/options establish the cross-session design; same-session tests are not described as two interactive Windows logons. Runtime hosting must acquire that owner gate before opening the production store.

Run `./tests/scripts/Test-PersistencePublish.ps1` (or pass `-Dotnet` for the isolated SDK) to publish and execute the production persistence modules as a self-contained win-x64 smoke. Its temporary project and profile stay under ignored `artifacts/persistence-publish`; it never opens the student's database or sends notifications. The probe checks private owner exclusion, atomic event creation, reopen/replay stability and actual app-local native SQLite loading. The script requires a successful child exit and matching structured results, preserves diagnostics, and runs in CI. It does not replace installed desktop acceptance.

Run `./tests/scripts/Test-NativeBuildOutput.ps1` for the standalone packaging regression. Fake tools verify that a stale SDK file from a prior build cannot enter a new installer, prior outputs are preserved, paths with spaces work, skip mode works and tool failures propagate. It performs no real publish, installer execution or native display. CI runs this alongside the foundation suites.

Run `./tests/scripts/Test-InstallerRecovery.ps1 -IsccPath 'C:\Path\To\ISCC.exe'` for installer policy checks. Development uses Inno Setup 6.7.3; CI logs its runner-provided compiler version. The script compiles and executes no-install Pascal fixtures using the production policy, exercises the real read-only Win32 process boundary, and compiles the production installer against a throwaway source tree. It never installs SNU Watcher or changes notification registration. Reports and fixtures stay under ignored `artifacts/installer-recovery-tests`. Native repair/uninstall acceptance still requires the VM.

## Windows acceptance boundary

See [native validation](native-validation.md) for interactive installer and notification steps. A passing unit test or successful notification API return does not establish that a banner was visible. Each native evidence record must distinguish OS accepted, banner seen, action handled, and whether the machine was clean of separately installed runtime dependencies.

Do not remove shared Windows App Runtime or OS CBS packages to simulate a clean machine. A fresh user account on this developer PC is not a clean VM. Clean install, restart, retained-toast activation, upgrade and uninstall remain release-blocking until recorded on a supported independent Windows environment.

Allow first-logon OS app provisioning to settle before recording the pre-install inventory. A framework package is not automatically an app-installed prerequisite: preserve evidence of its origin and timing, and distinguish it from the pinned SDK runtime. The evaluation image's App Readiness/ScreenSketch provisioning registered Framework 1.3 after an early inventory but before SNU Watcher setup.
