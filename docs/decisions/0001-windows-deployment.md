# Windows native feasibility and deployment

Status: implementation candidate. The clean-PC release gate is pending.

The M2 shell uses WPF on .NET 10, with the supported Windows App SDK 1.8 servicing line. The Windows helper library contains notification argument validation and same-user instance coordination; only the desktop executable references the notification runtime. This allows tests of the trust boundary without initializing COM or displaying a banner.

The application has a stable executable name (`SnuWatcher.Desktop.exe`), installer ID, and publisher identity (`DuckFard.SnuWatcher`). Installed files live under the current user's local application directory. The installer carries a complete self-contained publish folder, including .NET and native Windows App SDK files. It does not ask the student to install an SDK or run terminal commands.

The native sender explicitly sets its own `DuckFard.SnuWatcher` process AppUserModelID before resolving `AppNotificationManager.Default`, and diagnostics read it back. Without an explicit process identity, the SDK generates a path-based GUID; a published-folder test after uninstall is therefore not the same identity as a stable installed-path test. Never borrow PowerShell's or another application's identity. Test only one stable candidate installation at a time, since running a different copy with the same app identity can update its COM activation path.

The installer writes that AUMID to its own classic shortcut. After SDK registration the app reads the actual registered `CustomActivator` from its own per-user AUMID key and binds it to that existing shortcut's `System.AppUserModel.ToastActivatorCLSID`. It validates the shortcut target before touching properties and reloads persisted values to verify the write. It does not hardcode a borrowed CLSID or modify global notification preferences. This is a compatibility candidate prompted by an observed invisible-banner failure and an upstream issue; the student-visible and clean-VM tests remain required.

Windows local notifications use `AppNotificationManager`; handler registration precedes `Register()`, which precedes startup activation inspection. For unpackaged apps, the SDK owns notification COM registration. Do not use another application's PowerShell identity. Run unelevated, and report an administrator launch clearly.

There is a documentation disagreement: Microsoft's self-contained guide says local app notifications do not need Singleton, while API reference notes still state a dependency. Therefore folder publishing and a successful call on a developer machine are not sufficient evidence. M2 requires an interactive clean Windows 11 x64 VM with a before/after runtime inventory and foreground, tray, exited, and post-reboot click tests. If that profile fails, record a deployment redecision before M3 instead of quietly installing an undeclared prerequisite.

Ordinary exit calls `Unregister()`; uninstall calls `UnregisterAll()` in the original user's context before deleting files. An app notification body/action accepts only a small closed set of action values. Browser destinations are constants for official SNU/Genie pages, never an arbitrary URL supplied by a notification or pipe payload.

The single-instance transport uses a Windows named mutex and asynchronous pipe scoped to the Windows user SID and session. The pipe uses `CurrentUserOnly`, size limits, timeouts, and the same closed action parser. A second launch forwards a show request instead of creating another polling owner. This is app coordination, not a privileged command service.

References:

- [Microsoft .NET app notifications](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)
- [Microsoft self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [AppNotificationManager API reference](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager?view=windows-app-sdk-1.8)
- [Inno Setup](https://jrsoftware.org/isinfo.php)
- [Microsoft Shell AppUserModel.ID property](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id)
- [Microsoft Shell ToastActivatorCLSID property](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-toastactivatorclsid)
- [Upstream unpackaged notification Shell visibility report](https://github.com/microsoft/WindowsAppSDK/issues/6821)
