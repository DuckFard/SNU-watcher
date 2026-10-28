# Windows native feasibility and deployment

Status: implementation candidate with a protocol activation redecision on 2026-10-28. The clean-PC release gate is pending.

The M2 shell uses WPF on .NET 10, with the supported Windows App SDK 1.8 servicing line. The Windows helper library contains notification argument validation and same-user instance coordination; only the desktop executable references the notification runtime. This allows tests of the trust boundary without initializing COM or displaying a banner.

The application has a stable executable name (`SnuWatcher.Desktop.exe`), installer ID, and publisher identity (`DuckFard.SnuWatcher`). Installed files live under the current user's local application directory. The installer carries a complete self-contained publish folder, including .NET and native Windows App SDK files. It does not ask the student to install an SDK or run terminal commands.

The native sender explicitly sets its own `DuckFard.SnuWatcher` process AppUserModelID before resolving `AppNotificationManager.Default`, and diagnostics read it back. Without an explicit process identity, the SDK generates a path-based GUID; a published-folder test after uninstall is therefore not the same identity as a stable installed-path test. Never borrow PowerShell's or another application's identity. Test only one stable candidate installation at a time: SDK 1.8 can reuse an existing COM registration without refreshing its executable path, so another copy is not valid installed-path evidence.

The installer writes that AUMID to its own classic shortcut. After SDK registration the app reads the actual registered `CustomActivator` from its own per-user AUMID key and binds it to that existing shortcut's `System.AppUserModel.ToastActivatorCLSID`. It validates the shortcut target before touching properties and reloads persisted values to verify the write. It does not hardcode a borrowed CLSID or modify global notification preferences. This is a compatibility candidate prompted by an observed invisible-banner failure and an upstream issue; the student-visible and clean-VM tests remain required.

Windows local notifications use `AppNotificationManager`; handler registration precedes `Register()`, which precedes startup activation inspection. For unpackaged apps, the SDK owns notification COM registration. Do not use another application's PowerShell identity. Run unelevated, and report an administrator launch clearly.

## Activation redecision

The installed developer-PC candidate displayed a banner after its own shortcut identity was bound and the verifier accepted the Shell's canonical GUID string. The student then reported that both **Open registration** and **View app** failed on test notification 26146, even after startup was corrected to dispatch the SDK's first cold lifecycle payload. A direct own-class `CoCreateInstance` probe returned `REGDB_E_CLASSNOTREG` (`0x80040154`) before an app launch was observed. The merged 64-bit HKCR registration existed and its `LocalServer32` value pointed to the correct quoted installed executable and SDK launch marker. These observations do not establish a cause or a working COM callback. No global registration or notification-setting change is part of this decision.

The required student action is opening official SNU registration quickly. The revised test notification therefore uses supported raw toast XML with `activationType="protocol"` and the fixed official HTTPS destination in both the body's `launch` attribute and its sole **Open registration** button's `arguments` attribute. A pure helper builds the XML with `XElement`; callers cannot supply a URI. Windows launches the default browser directly, so this action does not depend on app cold activation or receipt of a callback. The optional **View app** notification button is removed. The app retains the SDK sender, stable own identity, verified own shortcut, and closed COM action parser as a legacy/best-effort path, without claiming that path works. No custom URI scheme or additional registry contract is introduced.

Foreground, tray, exited-process, persisted notification, and post-reboot checks now verify direct official HTTPS activation from both body and button. The app will not receive a protocol click diagnostic; browser observation is the acceptance evidence. This implementation change does not pass those manual checks or the independent clean-VM, runtime inventory, display, persisted-click, upgrade, or uninstall gates.

There is a documentation disagreement: Microsoft's self-contained guide says local app notifications do not need Singleton, while API reference notes still state a dependency. Therefore folder publishing and a successful call on a developer machine are not sufficient evidence. M2 requires an interactive independent clean Windows 11 x64 VM with a before/after runtime inventory and foreground, tray, exited, persisted, and post-reboot protocol click tests. If that deployment profile fails, record another deployment redecision before M3 instead of quietly installing an undeclared prerequisite.

Ordinary exit calls `Unregister()`; uninstall calls `UnregisterAll()` in the original user's context before deleting files. New registration notifications contain only the fixed official SNU HTTPS destination. The retained app callback/pipe path accepts only a small closed set of action values. Browser destinations are constants for official SNU/Genie pages, never an arbitrary URL supplied by a notification or pipe payload.

The single-instance transport uses a Windows named mutex and asynchronous pipe scoped to the Windows user SID and session. The pipe uses `CurrentUserOnly`, size limits, timeouts, and the same closed action parser. A second launch forwards a show request instead of creating another polling owner. This is app coordination, not a privileged command service.

References:

- [Microsoft .NET app notifications](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)
- [Microsoft self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [AppNotificationManager API reference](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager?view=windows-app-sdk-1.8)
- [Inno Setup](https://jrsoftware.org/isinfo.php)
- [Microsoft Shell AppUserModel.ID property](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id)
- [Microsoft Shell ToastActivatorCLSID property](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-toastactivatorclsid)
- [Upstream unpackaged notification Shell visibility report](https://github.com/microsoft/WindowsAppSDK/issues/6821)
- [Microsoft app notification content schema: protocol activation](https://learn.microsoft.com/en-us/windows/apps/design/shell/tiles-and-notifications/toast-schema)
- [Microsoft protocol button API](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.builder.appnotificationbutton.setinvokeuri?view=windows-app-sdk-1.8)
- [SDK 1.8 COM registration source](https://raw.githubusercontent.com/microsoft/WindowsAppSDK/release/1.8-stable/dev/AppNotifications/AppNotificationUtility.cpp)
