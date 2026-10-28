# Development progress

Evidence date: 2026-10-28, Asia/Seoul. This file distinguishes implemented source, developer-machine checks, and acceptance gates that require another environment. The [implementation plan](DESKTOP_APP_PLAN.md) remains the capability contract.

## Scope and status

- M0: complete. Owner selected DuckFard/SNU-watcher and MIT, excluded PDF import, and authorized development according to the plan.
- M1: source-contract foundation implemented. Anonymous SNU term/search/detail adapters, strict failure handling, and 55 offline tests are present. Bounded compiled-adapter live probes verified catalog/search/details, major options, and a combined positive filter query; the integration notes identify the remaining evidence boundaries.
- M2: candidate built and developer-machine lifecycle tested, but the native exit gate has NOT passed. The corrected installed candidate now shows a banner, confirmed by the user; its registration button failed and cold activation is being corrected. Clean-machine validation is also outstanding.
- M3-M8: not started. Per the plan, M3 waits for both M1 and M2 exit gates. There is no SQLite watch engine, polling UI, or public desktop release yet.

The existing Python watcher source is preserved under `legacy/`. Its runtime, personal watch configuration, state and processes were not changed by this foundation work.

## Toolchain and reproducibility

.NET SDK 10.0.403 is pinned in `global.json`. Development used an isolated workspace SDK rather than changing the system SDK installation. The Inno Setup 6.7.3 compiler was obtained from the official release and its Authenticode signature verified as valid for Pyrsys B.V. Dependency versions/lock files, offline tests, coverage, and a read-only CI workflow accompany the source.

## Native gate boundary

The available developer machine reports Windows 11 Home, build 26300, x64. It already contains multiple Windows App Runtime versions, including 1.4-1.8 and OS CBS packages. Standard Windows Sandbox, Hyper-V, VirtualBox, VMware and QEMU tooling was not available in the read-only environment inspection.

This machine can establish build, publish, local install and developer-session behavior, but cannot establish absence of undeployed runtime prerequisites. Do not uninstall shared or OS runtime packages to manufacture a clean test.

M2 remains open until the native protocol passes on a supported independent clean Windows 11 environment, including package inventories, visible banner/sound, action forwarding, process-exited activation, reboot, upgrade and uninstall cleanup. A successful API return is recorded as OS accepted, not banner seen. A fresh user account on this same PC does not satisfy the clean-machine requirement.

## Recorded developer evidence

- Locked solution restore and Release build passed with zero warnings and errors after stabilizing the Windows library's base/x64 lock graph.
- Source suite: 55 tests passed. The parent's final Release coverage run measured 95.03% lines and 86.61% branches.
- Windows helper suite after the cold-activation correction: 40 tests passed. The parent's final Release coverage run measured 87.50% lines and 85.10% branches. There are 95 passing offline tests across the two foundations; both exceed the 80% line/branch thresholds. WPF/native COM display is tested separately, not counted as a passing mocked banner test.
- The dependency vulnerability audit returned no known vulnerable packages, including transitive packages, for all five projects. CI parses the audit result and rejects reported vulnerabilities.
- The unsigned app-local installer compiled successfully. On this developer PC it installed, registered notifications, showed the validation window, forwarded a second launch, hid on window close, quit with owner acknowledgement, and completed a rerunnable setup with the owner stopped.
- Uninstall removed the installed executable and recorded `UnregisterAll` cleanup. This is developer-machine evidence; stale cold activation and clean-machine package independence still need the full protocol.
- The first published-folder notification test returned OS notification ID 26141; the human tester explicitly reported no banner. It was sent after uninstall from a different executable path, so it did not establish installed-path notification identity. The old path-based test registration was cleaned up before correcting the identity. Shell query state was `QUNS_ACCEPTS_NOTIFICATIONS`, which is diagnostic context, not proof of actual display.

The compatibility candidate now sets and reads back the app's own explicit process ID (`DuckFard.SnuWatcher`). Its installer-owned shortcut receives the matching AppUserModelID and the actual SDK-registered ToastActivatorCLSID. The code verifies the shortcut target, persists and reloads both properties, and refuses notification tests from another executable copy. Shell canonicalizes the activator property to a GUID string on this machine; readback now validates both native GUID and canonical string forms against the exact registered value. Independent source/interop review found no material remaining findings in that correction.

The installed-path diagnostic passed with Windows Setting Enabled and verified shortcut identity. The next test, notification ID 26144 at 15:35 Asia/Seoul, produced a banner the human tester confirmed seeing. The tester reported that the registration button did not work. This passes developer-PC visibility only. SDK source inspection identified a discarded cold-launch lifecycle payload; the revised app routes validated startup notification data explicitly and records lifecycle/activation failures separately. Seven new regression cases cover cold dispatch, ordinary launch, and malformed payloads. Installed click/cold activation remains pending retest. No API acceptance is substituted for human click evidence.

No production signing identity or signing secret is available; developer installers are unsigned and cannot be presented as public production releases. Record human-visible/click evidence separately from API acceptance. The source work and feasibility candidate can be reviewed while M2 remains open; do not advance the full watch engine under a false pass.
