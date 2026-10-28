# Native Windows validation

This is an M2 feasibility candidate, not a course-monitoring release. It intentionally does not start polling or show synthetic live course counts. The existing Python watcher is unchanged.

## Automated checks

The Windows helper tests cover the fixed official HTTPS destination in the toast body and sole button, valid test-labeled XML, a closed set of legacy notification actions, rejection of arbitrary URLs/commands/duplicate arguments, exact serialization, Windows COM launch-marker recognition, first cold lifecycle payload dispatch, same-user instance forwarding, owner release, and shutdown on a non-pumping UI synchronization context. Native banner visibility is not mocked into a success claim.

The test-first runs failed with missing activation, shortcut policy, cold lifecycle routing, protocol payload, elevation-policy, window-sizing and cleanup-policy types before implementation. The latest parent Release verification on 2026-10-29 passed 96 Windows tests with Cobertura coverage of 90.75% lines and 81.60% branches, alongside 55 source tests (96.64%/87.66%). Async state machines are now included in coverage; the earlier compiler-generated exclusion hid their logic. The standalone packaging regression also passed. Installer recovery adds 29 executable Pascal policy assertions, nine real Win32 probe assertions, and production Inno compilation. The restricted-token run could not connect its current-user-only named pipe; the same suite passed outside that token. The Desktop Release build succeeded with zero warnings and errors using workspace-local .NET SDK 10.0.403. These automated results do not establish clean-VM compatibility, visible banner, sound, or click activation.

An initial developer-PC notification experiment returned a nonzero notification ID with Windows Setting Enabled, but the student explicitly did not see a banner. It ran from a published directory after uninstall, so it was not sufficient installed-path evidence. The revised installed candidate adds an explicit own AUMID and installer shortcut metadata using the SDK's actual registered activator. After the shortcut read-back correction, installed registration succeeded and the student confirmed a visible banner. Both Open registration and View app buttons subsequently failed on notification 26146, after the cold lifecycle payload fix. A direct own-class `CoCreateInstance` probe returned `REGDB_E_CLASSNOTREG` (`0x80040154`) before an app launch was observed, despite a merged 64-bit HKCR `LocalServer32` entry with the correct quoted installed executable and SDK marker. The cause is unresolved; no additional global registration/settings changes follow from this evidence. A similar upstream [Shell-display issue report](https://github.com/microsoft/WindowsAppSDK/issues/6821) motivated the bounded display compatibility fix; it does not prove the cause on this machine.

The [activation redecision](decisions/0001-windows-deployment.md) makes registration clicks direct Windows protocol actions. The new test payload uses raw toast XML with `activationType="protocol"` and the same fixed official SNU HTTPS destination for its body and sole Open registration button. It has no View app button, caller-supplied URI, or custom scheme. The SDK still displays the notification under the app's own identity. Clicking now asks Windows to open the default browser directly and does not require an app callback. On 2026-10-29 the human tester confirmed the installed button on test 26152 and body on separate test 26154 each opened official SNU registration. No SNU Watcher process remained after the clicks. This is developer-PC evidence only; independent clean-VM, persisted/post-reboot and full lifecycle checks remain pending.

Use the repository's foundation test command, or run:

```powershell
dotnet test tests/SnuWatcher.Windows.Tests/SnuWatcher.Windows.Tests.csproj -c Release
```

Build the self-contained folder and unsigned candidate installer:

```powershell
.\scripts\Build-Native.ps1 -DotNetPath dotnet -IsccPath 'C:\Path\To\ISCC.exe'
```

No PowerShell AUMID, credential, arbitrary URI, or remote script is used by the app. Microsoft.WindowsAppSDK is now pinned to current stable 2.6.4; the earlier developer-PC evidence above was collected with 1.8.260921001. Independent 2.6.4 VM evidence and its remaining gates are recorded below. A custom untrusted URI is never accepted by the notification/pipe parser. Installer Quit is a distinct same-user local action which notification arguments cannot invoke. Unsupported elevation has a typed failure with fixed normal-launch guidance before native registration.

Every build uses a fresh `artifacts/native/<GUID>/publish` and installer directory, printed by the build script. Inno requires explicit source/output defines supplied by that script. Do not reuse an older SDK publish folder or interpret its historical installer hash as evidence for a new binary.

The sender sets and reads back its own explicit process AppUserModelID (`DuckFard.SnuWatcher`) before obtaining the SDK notification manager. After Register, it reads that identity's actual `CustomActivator` and binds both `System.AppUserModel.ID` and `System.AppUserModel.ToastActivatorCLSID` to the existing installer-owned Start Menu shortcut. It verifies the target equals the running executable and reloads both properties from disk. Diagnostics include the identity and shortcut-binding status. Always test from the stable installed location; SDK 1.8 may reuse an existing COM server path rather than refresh it for a different copy. A noninstalled or mismatched shortcut blocks test submission. Do not test a deleted/uninstalled path and interpret that as installation evidence.

An installed-candidate diagnostic exposed a shortcut read-back regression: the Shell returned the correct activator GUID as a canonical string, while the verifier initially accepted only `VT_CLSID`. The revised verifier accepts `VT_CLSID` and GUID strings stored as `VT_LPWSTR` or `VT_BSTR`, then requires the parsed GUID to equal the SDK's actual registered activator. A binding failure reports bounded variant-type codes plus the exception type and HRESULT, without collecting private paths. Installed registration and developer-PC banner visibility subsequently succeeded; this does not pass the clean-VM gate.

The retained legacy COM path had a separate routing bug: startup discarded `GetActivatedEventArgs()`. The [SDK 1.8 implementation](https://raw.githubusercontent.com/microsoft/WindowsAppSDK/release/1.8-stable/dev/AppNotifications/AppNotificationManager.cpp) stores that first payload for lifecycle retrieval rather than raising `NotificationInvoked`; the [official quickstart](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart) handles its `AppNotification` kind and typed data. Startup now dispatches that payload through the closed action parser; later callbacks use the notification event. Its bounded diagnostics remain best-effort and do not establish working COM activation. New registration notifications do not use this path. Normal exit retains `Unregister()`; persistent `UnregisterAll()` cleanup remains confined to uninstall.

## Developer smoke commands

Install the candidate first, then run these against its stable installed executable path, without administrator elevation:

```powershell
$snuWatcherExecutable = Join-Path $env:LOCALAPPDATA 'Programs\DuckFard\SNU Watcher\SnuWatcher.Desktop.exe'
.\scripts\Test-NativeSmoke.ps1 -ExecutablePath $snuWatcherExecutable
.\scripts\Test-NativeSmoke.ps1 -ExecutablePath $snuWatcherExecutable -Mode TestNotification
```

If installation used a custom folder, replace `$snuWatcherExecutable` with that installed executable's full path. Do not use the build's `artifacts\native\publish` copy for these checks.

The first writes registration diagnostics and exits. The second submits a clearly labeled test popup and exits after three seconds, enabling a direct HTTPS click test after the sender has exited. Test the body and button on separate notifications. Windows handles those protocol clicks without starting SNU Watcher, so no app activation report is expected: confirm the official destination visibly in the browser. Diagnostic/test/cleanup modes reject an already running candidate with a nonzero exit code; quit it first. `--quit` forwards a same-user graceful quit, receives the owner PID acknowledgement, and waits up to ten seconds for that process to exit; timeout/failure is nonzero. Installer upgrade/uninstall aborts on that failure. `--unregister-notifications` cleans native registration before uninstall. The smoke script validates a fresh timestamp and the expected operation stage. Diagnostics are stored only in `%LOCALAPPDATA%/DuckFard/SnuWatcher/native-diagnostics.json` and always retain `visibleBannerConfirmed=false` and `cleanVmGatePassed=false`: those require human/VM evidence.

## Required interactive clean-PC checklist

Status: **pending**. Do not mark M2 complete or proceed to M3 merely because build/tests or a developer-PC API call succeed.

The independent evaluation VM is prepared on 2026-10-29. Its pre-app inventory records Windows 11 Enterprise Evaluation 26300.9457, ordinary Medium-integrity Explorer context, no .NET command/SDK directories, and only the two OS CBS runtime packages. Guest Additions and separate App Runtime installations are absent. A clean powered-off snapshot is preserved.

The first automated installation attempt of candidate `0BC8797F85538D6373005F3B792713402439917BB58361B914CE54078DB0C8A0` did not pass. The helper's default media-directory binding failed under Windows PowerShell 5.1; a real `-File` binding regression now covers the correction. Subsequent explicit-directory checks observed an incomplete installed folder (265 top-level entries versus 406 published files) and missing `System.Private.CoreLib.dll`. The .NET host exited before managed app initialization or a diagnostic report. This does not establish a Windows notification or undeclared-runtime dependency failure. Private evidence is saved; the restored-snapshot visible installation below completed, and the original partial-install cause remains unestablished. No additional runtime is installed to mask the result.

A restored-snapshot visible installation subsequently reached the completion page and launched the candidate with verified shortcut identity. Windows DND was initially on. After disabling DND and its automatic rules only in the guest, test 8 produced a visible banner; separate button/body clicks opened `https://sugang.snu.ac.kr/sugang/co/co010.action` in the default browser. Sound remains unverified: VirtualBox's subsequent recorder stop timed out and left an incomplete capture. Do not interpret that recorder failure as proof of app silence. The current candidate's initial window also exceeded the 125% desktop work area; the source correction awaits rebuilt-candidate verification.

The early pre-app inventory was taken during Windows' asynchronous first-logon package registration. A later inventory contains Framework 1.3 (`3000.934.1904.0`) as well as the two CBS packages. AppX deployment records and the installed/updated date place its App Readiness/ScreenSketch registration before SNU Watcher setup. This is evidence of pre-existing OS provisioning, not an app prerequisite install. Collect a settled pre-install inventory for the next candidate, preserve these distinctions, and never remove OS packages to manufacture a clean test.

The installer recovery policy permits replacing a broken EXE only when its bounded, read-only native process probe proves the installed owner absent. Unknown or inaccessible matching owners block repair. Uninstall requires successful quit, awaited retained-notification removal, and persistent native unregister before file deletion; an unstartable app requires repair first. Removal has a ten-second deadline, and incomplete registration, failure or timeout prevents success. SDK registration cleanup alone does not guarantee clearing Notification Center. If uninstall is canceled after that preflight, a normal relaunch registers notifications again. Executable Pascal fixtures exercise the actual policy and native Win32 boundary; partial-install repair and normal uninstall still need VM acceptance on the rebuilt candidate.

For practical setup steps on the current Windows Home laptop, use the [clean Windows VM guide](clean-windows-validation.md).

1. Start a supported independent clean Windows 11 x64 VM, not a fresh account on the same developer host. It must have no .NET SDK, developer PowerShell, or separately installed Windows App Runtime packages. Distinguish built-in OS component packages from the pinned SDK runtime; save the OS build and `Get-AppxPackage Microsoft.WindowsAppRuntime*` inventory before installation.
2. Install the candidate EXE as an ordinary user. Confirm no undeclared runtime/prerequisite installer ran; inventory runtime packages afterward. Verify the stable install path and Start Menu entry.
3. Launch normally, send separate tests for body and button, and record actual visible banner/app identity and expected Windows sound (subject to user settings). Confirm both direct protocol actions open the official HTTPS destination in the default browser.
4. Hide to tray and repeat body/button HTTPS checks. Relaunch the EXE and confirm one owner/window. Double-click the tray icon to restore it. Quit and confirm the tray process stops promptly.
5. Submit notifications for separate body/button checks, exit the app, and click each from Notification Center. Confirm the official HTTPS destination opens with no SNU Watcher callback or process required. Preserve a notification across reboot and verify its protocol click, then launch the installed app and repeat display/body/button tests. Record persisted and newly submitted results separately.
6. Disable notifications or enable DND and verify honest diagnostics. Launch as administrator and verify a clear unsupported-elevation diagnostic, never a false readiness claim.
7. Rerun the installer while the app is in the tray: graceful quit, stable identity/path, successful replacement. Uninstall and confirm no stale notification/COM activation remains. Local diagnostics are not course/watch data and may be removed separately.
8. Record results, OS/runtime inventory, candidate SHA-256, and screenshots in a private validation log. Do not commit account names, local paths, or student data as evidence.

If app-local notification deployment fails on the clean VM, preserve the failure evidence and make the explicit deployment redecision described in ADR 0001 before M3. No signed public release is authorized by a successful M2 experiment.
