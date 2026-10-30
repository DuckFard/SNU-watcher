# Test the candidate in a clean Windows VM

Guide checked on 2026-10-29, Asia/Seoul. **No clean-machine result has been recorded.** This is a setup guide for the M2 feasibility candidate, which does not monitor courses yet. The authoritative acceptance checklist is [Native Windows validation](native-validation.md#required-interactive-clean-pc-checklist).

A Windows VM on the current laptop is sufficient; another physical computer is optional. GitHub CI checks builds and offline behavior, but cannot establish what a student sees or clicks. Windows Sandbox is an alternative on supported editions, [including Pro, Enterprise and Education, but not Home](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/). A fresh account on the existing developer PC still shares its installed runtimes.

## 1. Prepare the VM

On the laptop, check **Settings → System → About** for installed RAM and **Task Manager → Performance → CPU** for virtualization. Our practical starting recommendation is a host with 16 GB RAM and at least 100 GB free disk space for the guest, ISO and a snapshot. Leave enough resources for the laptop; snapshot growth can require more space. If virtualization is unavailable or VirtualBox cannot start, record the error before changing firmware or host security settings.

When ready to set it up:

1. Get the current stable **Windows x64 base installer** from [Oracle's official VirtualBox download page](https://www.oracle.com/virtualization/technologies/vm/downloads/virtualbox-downloads.html). The optional Extension Pack is unnecessary for this test. Installing VirtualBox changes the host; these instructions do not perform that installation.
2. Obtain a Windows 11 **x64** ISO. Microsoft's [Enterprise evaluation](https://www.microsoft.com/en-us/evalcenter/evaluate-windows-11-enterprise) provides a registered 90-day evaluation under its terms. Use the regular release for this test, and record its edition/build. A separately licensed Windows 11 ISO is another option. Install Windows **inside the VM**, not onto the laptop.
3. In VirtualBox choose **Home → New**, name it `SNU Watcher Clean`, select the ISO, and verify Windows 11 (64-bit). Store VM files outside the Git checkout. Deselect **Install OS Using Unattended Installation** to follow Windows setup interactively. [Oracle's creation guide](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/create-vm.html) explains the wizard.

Use these project settings:

| Setting | Value |
| --- | --- |
| Guest memory | 6 GB if the host can spare it; never below Windows 11's 4 GB minimum |
| Processors | 2 virtual CPUs; leave at least half the host's processor threads available |
| Virtual disk | 80 GB, dynamically allocated; Windows 11 requires at least 64 GB |
| Firmware/security | UEFI, Secure Boot, TPM 2.0 |
| Network/audio | Default NAT networking; audio output enabled with the default host driver |

With the VM powered off, verify firmware/security under **Settings → System → Motherboard**; switch VirtualBox to Expert experience if needed. Keep the Windows 11 template's other defaults. These are [Windows 11 requirements](https://learn.microsoft.com/en-us/windows/whats-new/windows-11-requirements) and [VirtualBox settings](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/working-with-vms.html).

Complete Windows setup and updates, then restart the guest. Do not install Visual Studio, a .NET SDK, PowerShell 7, Windows App Runtime, or runtime repair packages. Defer optional Guest Additions until the first baseline test; if they become necessary, record that change. Built-in Windows PowerShell and Edge are enough.

## 2. Save a clean baseline

Inside the guest, open ordinary **Windows PowerShell**. Run this block before installing SNU Watcher. Later, change `before` to `after` and run it again after installation:

```powershell
$snuValidationPhase = 'before'
$snuEvidenceFolder = Join-Path $env:USERPROFILE 'Desktop\SNUWatcher-validation'
New-Item -ItemType Directory -Path $snuEvidenceFolder -Force | Out-Null

Get-ComputerInfo |
    Select-Object WindowsProductName, WindowsVersion, OsBuildNumber, OsArchitecture |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $snuEvidenceFolder ($snuValidationPhase + '-windows.json')) -Encoding UTF8

$snuRuntimePackages = @(Get-AppxPackage |
    Where-Object { $_.Name -match 'WindowsAppRuntime|WinAppRuntime' } |
    Sort-Object Name |
    Select-Object Name, @{ Name = 'Version'; Expression = { $_.Version.ToString() } }, PackageFullName)
ConvertTo-Json -InputObject $snuRuntimePackages -Depth 4 |
    Set-Content -LiteralPath (Join-Path $snuEvidenceFolder ($snuValidationPhase + '-runtimes.json')) -Encoding UTF8

Get-Command dotnet -ErrorAction SilentlyContinue |
    Select-Object Name, Source |
    Out-File -LiteralPath (Join-Path $snuEvidenceFolder ($snuValidationPhase + '-dotnet.txt')) -Encoding UTF8
```

An empty runtime array is valid evidence. Built-in **CBS** packages can already be present: retain and identify them. Framework, Main or Singleton packages need separate review; do not delete packages to make the inventory look clean. An existing matching runtime can conceal a missing installer dependency. The `dotnet` command is normally absent in this baseline; if present, investigate rather than assuming the VM is clean.

Shut down Windows, then use **Snapshots → Take** in VirtualBox and name it `Clean Windows before SNU Watcher`. This saves a reusable pre-install state. [Restoring a snapshot reverts guest changes](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/working-with-vms.html), so export evidence before restoring it.

## 3. Transfer and install the exact candidate

Get the candidate installer and its matching SHA-256 from the developer. The GitHub source ZIP is not an installer. Transfer it using an approved download in the guest browser or a prepared read-only transfer ISO attached to the VM's optical drive. This avoids requiring guest development tools or shared folders for the initial test.

Copy the installer into the guest's Downloads folder. For the current M2 filename, verify it with:

```powershell
$snuCandidatePath = Join-Path $env:USERPROFILE 'Downloads\SnuCourseWatcher-0.1.0-m2-windows-x64-Setup.exe'
Get-FileHash -LiteralPath $snuCandidatePath -Algorithm SHA256
```

Compare the complete hash with the value supplied for that build. If the candidate filename changes, update the filename in the command. The M2 candidate is unsigned; record Windows reputation or installation prompts without disabling protection or changing global trust settings. If installation is blocked, capture the error and report the remaining cases as untested.

Double-click the verified installer as an ordinary user. Keep the default per-user folder and launch the installed **SNU Watcher** shortcut normally, without **Run as administrator**. Run the inventory block again with `$snuValidationPhase = 'after'`. Record any prerequisite prompt or newly installed runtime.

## 4. Run the acceptance checks

Follow the full [interactive checklist](native-validation.md#required-interactive-clean-pc-checklist), recording a result for each case:

| Case | What to observe |
| --- | --- |
| Visible app | **Send test notification** produces the app-owned banner and expected sound under the current Windows settings |
| Body and button | Use separate notifications: clicking the body and **Open registration** each opens the official SNU HTTPS page |
| Tray and second launch | Closing hides to tray; reopening focuses the same app; repeat both notification actions |
| App exited | Use **Quit application**, then click retained notifications from Notification Center; the browser opens without restarting SNU Watcher |
| Guest reboot | Restart Windows inside the VM; check a retained notification separately from a newly sent test |
| Setup lifecycle | Rerun setup while the app is in the tray; verify graceful shutdown, stable install path, then uninstall cleanup |

No SNU login is needed to verify the destination. A successful API message is not evidence that a banner appeared or a click worked. If a retained notification does not survive reboot, record that result explicitly; a newly sent one does not substitute for it.

Keep the candidate hash, Windows/VirtualBox versions, before/after inventories, results and screenshots in the private evidence folder. Export them outside the Git checkout before restoring the snapshot. Do not commit account details, VM images, diagnostics or screenshots. Report failed and untested cases as such; this guide does not mark M2 passed or authorize a public release.
