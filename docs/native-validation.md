# Native Windows validation

This is an M2 feasibility candidate, not a course-monitoring release. It intentionally does not start polling or show synthetic live course counts. The existing Python watcher is unchanged.

## Automated checks

The Windows helper tests cover a closed set of notification actions, rejection of arbitrary URLs/commands/duplicate arguments, exact serialization, Windows COM launch-marker recognition, first cold lifecycle payload dispatch, same-user instance forwarding, owner release, and shutdown on a non-pumping UI synchronization context. Native banner visibility is not mocked into a success claim.

Developer evidence on 2026-10-28: the test-first runs failed with missing activation, shortcut policy, and cold lifecycle routing types before implementation; the updated Release run passed 40 tests. Windows helper coverage was 87.50% lines and 85.10% branches. The restricted-token run could not connect its current-user-only named pipe; the same suite passed outside that token. The Desktop Release build succeeded with zero warnings and errors using workspace-local .NET SDK 10.0.403. These automated results do not establish clean-VM compatibility, visible banner, sound, or click activation.

An initial developer-PC notification experiment returned a nonzero notification ID with Windows Setting Enabled, but the student explicitly did not see a banner. It ran from a published directory after uninstall, so it was not sufficient installed-path evidence. The revised installed candidate adds an explicit own AUMID and installer shortcut metadata using the SDK's actual registered activator. After the shortcut read-back correction, installed registration succeeded and the student confirmed a visible banner. Its Open registration button did not work after the test process exited; cold click activation remains unverified. A similar upstream [Shell-display issue report](https://github.com/microsoft/WindowsAppSDK/issues/6821) motivated the bounded compatibility fix; it does not prove the cause on this machine.

Use the repository's foundation test command, or run:

```powershell
dotnet test tests/SnuWatcher.Windows.Tests/SnuWatcher.Windows.Tests.csproj -c Release
```

Build the self-contained folder and unsigned candidate installer:

```powershell
.\scripts\Build-Native.ps1 -DotNetPath dotnet -IsccPath 'C:\Path\To\ISCC.exe'
```

No PowerShell AUMID, credential, arbitrary URI, or remote script is used by the app. Microsoft.WindowsAppSDK is pinned to 1.8.260921001. A custom untrusted URI is never accepted by the notification/pipe parser. Installer Quit is a distinct same-user local action which notification arguments cannot invoke.

The sender sets and reads back its own explicit process AppUserModelID (`DuckFard.SnuWatcher`) before obtaining the SDK notification manager. After Register, it reads that identity's actual `CustomActivator` and binds both `System.AppUserModel.ID` and `System.AppUserModel.ToastActivatorCLSID` to the existing installer-owned Start Menu shortcut. It verifies the target equals the running executable and reloads both properties from disk. Diagnostics include the identity and shortcut-binding status. Always test from the stable installed location; running another copy with the same identity may update COM activation to that copy. A noninstalled or mismatched shortcut blocks test submission. Do not test a deleted/uninstalled path and interpret that as installation evidence.

An installed-candidate diagnostic exposed a shortcut read-back regression: the Shell returned the correct activator GUID as a canonical string, while the verifier initially accepted only `VT_CLSID`. The revised verifier accepts `VT_CLSID` and GUID strings stored as `VT_LPWSTR` or `VT_BSTR`, then requires the parsed GUID to equal the SDK's actual registered activator. A binding failure reports bounded variant-type codes plus the exception type and HRESULT, without collecting private paths. Installed registration and developer-PC banner visibility subsequently succeeded; this does not pass the clean-VM gate.

The first cold click had a separate routing bug: startup discarded `GetActivatedEventArgs()`. The [SDK 1.8 implementation](https://raw.githubusercontent.com/microsoft/WindowsAppSDK/release/1.8-stable/dev/AppNotifications/AppNotificationManager.cpp) stores that first payload for lifecycle retrieval rather than raising `NotificationInvoked`; the [official quickstart](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart) handles its `AppNotification` kind and typed data. Startup now dispatches that payload through the same closed action parser. Later clicks still use the notification event. Bounded diagnostics distinguish lifecycle retrieval, accepted action, rejected/missing payload, and browser-launch failure. Actual installed cold click behavior must be retested. Normal exit retains `Unregister()`, whose [documented behavior](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.unregister?view=windows-app-sdk-1.8) permits later clicks to start a new process; persistent `UnregisterAll()` cleanup remains confined to uninstall.

## Developer smoke commands

Install the candidate first, then run these against its stable installed executable path, without administrator elevation:

```powershell
$snuWatcherExecutable = Join-Path $env:LOCALAPPDATA 'Programs\DuckFard\SNU Watcher\SnuWatcher.Desktop.exe'
.\scripts\Test-NativeSmoke.ps1 -ExecutablePath $snuWatcherExecutable
.\scripts\Test-NativeSmoke.ps1 -ExecutablePath $snuWatcherExecutable -Mode TestNotification
```

If installation used a custom folder, replace `$snuWatcherExecutable` with that installed executable's full path. Do not use the build's `artifacts\native\publish` copy for these checks.

The first writes registration diagnostics and exits. The second submits a clearly labeled test popup and exits after three seconds, enabling a cold-click experiment. Diagnostic/test/cleanup modes reject an already running candidate with a nonzero exit code; quit it first. `--quit` forwards a same-user graceful quit, receives the owner PID acknowledgement, and waits up to ten seconds for that process to exit; timeout/failure is nonzero. Installer upgrade/uninstall aborts on that failure. `--unregister-notifications` cleans native registration before uninstall. The smoke script validates a fresh timestamp and the expected operation stage. Diagnostics are stored only in `%LOCALAPPDATA%/DuckFard/SnuWatcher/native-diagnostics.json` and always retain `visibleBannerConfirmed=false` and `cleanVmGatePassed=false`: those require human/VM evidence.

## Required interactive clean-PC checklist

Status: **pending**. Do not mark M2 complete or proceed to M3 merely because build/tests or a developer-PC API call succeed.

1. Start a supported independent clean Windows 11 x64 VM, not a fresh account on the same developer host. It must have no .NET SDK, developer PowerShell, or separately installed Windows App Runtime packages. Distinguish built-in OS component packages from the pinned SDK runtime; save the OS build and `Get-AppxPackage Microsoft.WindowsAppRuntime*` inventory before installation.
2. Install the candidate EXE as an ordinary user. Confirm no undeclared runtime/prerequisite installer ran; inventory runtime packages afterward. Verify the stable install path and Start Menu entry.
3. Launch normally, send a test, and record actual visible banner/app identity and expected Windows sound (subject to user settings). Open registration from body and button and confirm the official HTTPS destination.
4. Hide to tray, relaunch the EXE, and confirm one owner/window. Double-click the tray icon to restore it. Quit and confirm the tray process stops promptly.
5. Submit a notification, exit the app, and click it. Confirm cold activation opens the official destination without starting duplicate polling or a hidden extra owner. Reboot and test notification registration/click activation again.
6. Disable notifications or enable DND and verify honest diagnostics. Launch as administrator and verify a clear unsupported-elevation diagnostic, never a false readiness claim.
7. Rerun the installer while the app is in the tray: graceful quit, stable identity/path, successful replacement. Uninstall and confirm no stale notification/COM activation remains. Local diagnostics are not course/watch data and may be removed separately.
8. Record results, OS/runtime inventory, candidate SHA-256, and screenshots in a private validation log. Do not commit account names, local paths, or student data as evidence.

If app-local notification deployment fails on the clean VM, preserve the failure evidence and make the explicit deployment redecision described in ADR 0001 before M3. No signed public release is authorized by a successful M2 experiment.
