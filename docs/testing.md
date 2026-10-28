# Foundation verification

The source-contract and native Windows foundations use xUnit/VSTest with fake HTTP and real local IPC where appropriate. Tests never log into SNU, submit registrations, or send native notifications. The shared runsettings measure Infrastructure and Windows helper logic; WPF/Windows App SDK display and activation require the separate interactive gate.

Use the SDK pinned in `global.json`. Restore once to establish lock files during development, then run the locked verification script:

```powershell
dotnet restore SnuWatcher.slnx
./scripts/Test-Foundation.ps1
```

If the SDK is installed outside PATH, pass its executable to `-Dotnet`. The script performs a Release build, runs both offline suites, writes TRX and Cobertura reports under ignored `artifacts/test-results`, and requires at least 80% line and branch coverage for each foundation suite. `-SkipCoverageGate` is diagnostic only; it does not satisfy the milestone gate.

Dependency versions are pinned in project files and `packages.lock.json`. CI uses locked restore, commit-pinned GitHub actions, and no SNU requests. An audit listing is evidence for review, not an assertion that the command automatically fails on every advisory.

## Windows acceptance boundary

See [native validation](native-validation.md) for interactive installer and notification steps. A passing unit test or successful notification API return does not establish that a banner was visible. Each native evidence record must distinguish OS accepted, banner seen, action handled, and whether the machine was clean of separately installed runtime dependencies.

Do not remove shared Windows App Runtime or OS CBS packages to simulate a clean machine. A fresh user account on this developer PC is not a clean VM. Clean install, restart, retained-toast activation, upgrade and uninstall remain release-blocking until recorded on a supported independent Windows environment.
