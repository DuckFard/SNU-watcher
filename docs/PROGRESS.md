# Development progress

Updated: 2026-10-29, Asia/Seoul. Earlier observations below were recorded on 2026-10-28. This file distinguishes implemented source, developer-machine checks, and acceptance gates that require another environment. The [implementation plan](DESKTOP_APP_PLAN.md) remains the capability contract.

## Scope and status

- M0: complete. Owner selected DuckFard/SNU-watcher and MIT, excluded PDF import, and authorized development according to the plan.
- M1: exit evidence complete for the documented v1 source scope and explicit fallbacks. Anonymous SNU term/search/detail adapters and 55 offline tests are present. Bounded compiled-adapter live probes verified catalog/search/details and, on 2026-10-29, instructor and ordinary-department filters independently. Major filtering and deep links remain disabled/unverified paths as recorded in the integration notes.
- M2: native exit gate has NOT passed. The earlier 1.8 candidate's installed banner, direct registration button, and popup body click are human-confirmed working on this developer PC. The supported 2.6.4 upgrade has passed build/tests; its native candidate and independent clean-machine, persisted/post-reboot and remaining acceptance checks are pending.
- M3-M8: not started. Per the plan, M3 waits for both M1 and M2 exit gates. There is no SQLite watch engine, polling UI, or public desktop release yet.

The existing Python watcher source is preserved under `legacy/`. Its runtime, personal watch configuration, state and processes were not changed by this foundation work.

## Toolchain and reproducibility

.NET SDK 10.0.403 is pinned in `global.json`. Development used an isolated workspace SDK rather than changing the system SDK installation. The Inno Setup 6.7.3 compiler was obtained from the official release and its Authenticode signature verified as valid for Pyrsys B.V. Dependency versions/lock files, offline tests, coverage, and a read-only CI workflow accompany the source.

## Native gate boundary

The available developer machine reports Windows 11 Home, build 26300, x64. It already contains multiple Windows App Runtime versions, including 1.4-1.8 and OS CBS packages. Standard Windows Sandbox, Hyper-V, VirtualBox, VMware and QEMU tooling was not available in the read-only environment inspection.

This machine can establish build, publish, local install and developer-session behavior, but cannot establish absence of undeployed runtime prerequisites. Do not uninstall shared or OS runtime packages to manufacture a clean test.

M2 remains open until the native protocol passes on a supported independent clean Windows 11 environment, including package inventories, visible banner/sound, direct browser body/button clicks while running/in-tray/exited, reboot, ordinary single-instance forwarding, upgrade and uninstall cleanup. A successful API return is recorded as OS accepted, not banner seen. A fresh user account on this same PC does not satisfy the clean-machine requirement.

## Recorded developer evidence

- Locked solution restore and Release build passed with zero warnings and errors after stabilizing the Windows library's base/x64 lock graph.
- Source suite: 55 tests passed. The parent's final Release coverage run measured 95.03% lines and 86.61% branches.
- Windows helper suite after the protocol and elevation-policy changes: 46 tests passed. The parent's final Release coverage run measured 89.88% lines and 85.71% branches. There are 101 passing offline tests across the two foundations; both exceed the 80% line/branch thresholds. The standalone packaging regression also passed stale-file isolation, preservation, space-path, skip and failure scenarios. WPF/native display and browser clicks are tested separately, not counted as passing mocked banner tests.
- The dependency vulnerability audit returned no known vulnerable packages, including transitive packages, for all five projects. CI parses the audit result and rejects reported vulnerabilities.
- The unsigned app-local installer compiled successfully. On this developer PC it installed, registered notifications, showed the validation window, forwarded a second launch, hid on window close, quit with owner acknowledgement, and completed a rerunnable setup with the owner stopped.
- Uninstall removed the installed executable and recorded `UnregisterAll` cleanup. This is developer-machine evidence; stale cold activation and clean-machine package independence still need the full protocol.
- The first published-folder notification test returned OS notification ID 26141; the human tester explicitly reported no banner. It was sent after uninstall from a different executable path, so it did not establish installed-path notification identity. The old path-based test registration was cleaned up before correcting the identity. Shell query state was `QUNS_ACCEPTS_NOTIFICATIONS`, which is diagnostic context, not proof of actual display.

The compatibility candidate now sets and reads back the app's own explicit process ID (`DuckFard.SnuWatcher`). Its installer-owned shortcut receives the matching AppUserModelID and the actual SDK-registered ToastActivatorCLSID. The code verifies the shortcut target, persists and reloads both properties, and refuses notification tests from another executable copy. Shell canonicalizes the activator property to a GUID string on this machine; readback now validates both native GUID and canonical string forms against the exact registered value. Independent source/interop review found no material remaining findings in that correction.

The installed-path diagnostic passed with Windows Setting Enabled and verified shortcut identity. Notification ID 26144 at 15:35 Asia/Seoul produced a banner the human tester confirmed seeing, but its registration button failed. SDK source inspection identified a discarded cold-launch lifecycle payload; that path was corrected with seven regression cases. The subsequent installed test 26146 still had both buttons fail, as reported by the human tester. A direct own-class probe returned Class not registered before an app launch was observed despite the expected merged 64-bit per-user COM server entry. The underlying COM failure remains unresolved.

ADR 0001 and the plan now explicitly choose direct HTTPS protocol activation for both the popup body and its sole Open registration button. The app builds fixed-destination XML with no caller-supplied URI. Browser launch no longer depends on the failed app callback; no app click receipt is claimed. Four payload regressions pass.

On 2026-10-29 the revised installer completed setup and fresh registration diagnostics. The human tester confirmed that Open registration on test 26152 opened the official SNU page, and that clicking the body of a separate test 26154 also opened it. Both CLI test processes exited before the confirmation requests; a subsequent process check found no SNU Watcher process restarted. This establishes developer-PC body/button behavior, not the full foreground/tray/reboot/clean-machine matrix. Candidate SHA-256: `B76D5FC049CC0439744E27092EE38A2667B3354FC32AA9C7B7A163C0AA5AC45F`. No API acceptance is substituted for human click evidence.

The foundation is saved on `development/windows-foundation` in [draft PR 1](https://github.com/DuckFard/SNU-watcher/pull/1). The 99-test protocol commit passed [GitHub Windows CI](https://github.com/DuckFard/SNU-watcher/actions/runs/40720100097), including build, publish and dependency audit. The subsequent 101-test supported-SDK/elevation/packaging changes have passed the local foundation gate and await their next CI run.

## Supported SDK and clean-VM preparation

The original native candidate used Windows App SDK 1.8.260921001. Microsoft's release table still labels that line Maintenance while listing a servicing end date of 2026-09-24. The desktop project and lock file now pin the verified current stable **2.6.4**, released 2026-10-27, in the supported 2.x line. Locked restore and Release build passed with no API rewrite. The historical B76 candidate's click evidence remains evidence for 1.8; the upgraded binary needs native validation. Each new native build now gets a fresh GUID directory to prevent removed SDK files surviving an overlay publish. Historical candidate files remain untouched.

The owner authorized VirtualBox installation and preparation of an independent clean Windows test VM on 2026-10-29. Oracle VirtualBox 7.2.22's official checksum and Authenticode signature were verified; the minimal core/NAT-capable feature installed successfully with no host restart required. The new VM's 18 configuration checks and a firmware-only startup passed. It is powered off with an empty disk; no guest OS or guest utilities have been installed. The official Windows evaluation ISO is downloading and will be accepted only after matching Microsoft's published digest. VM images, logs, future test credentials and evidence remain private and outside Git. The [VM guide](clean-windows-validation.md) describes the remaining acceptance work. M2 is still open.

No production signing identity or signing secret is available; developer installers are unsigned and cannot be presented as public production releases. Record human-visible/click evidence separately from API acceptance. The source work and feasibility candidate can be reviewed while M2 remains open; do not advance the full watch engine under a false pass.
