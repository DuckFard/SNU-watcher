; M2 unsigned feasibility candidate. Sign only in an approved release workflow.
#ifndef SourceDir
  #define SourceDir "..\artifacts\native\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\native\installer"
#endif
#define AppExe "SnuWatcher.Desktop.exe"

[Setup]
AppId={{9F9D8A03-BEA5-4CC9-AB54-33AB43B8E9C4}
AppName=SNU Watcher
AppVersion=0.1.0
AppVerName=SNU Watcher 0.1.0 M2 feasibility candidate
AppPublisher=DuckFard
AppPublisherURL=https://github.com/DuckFard/SNU-watcher
DefaultDirName={localappdata}\Programs\DuckFard\SNU Watcher
DefaultGroupName=SNU Watcher
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=SnuCourseWatcher-0.1.0-m2-windows-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SNU Watcher"; Filename: "{app}\{#AppExe}"; AppUserModelID: "DuckFard.SnuWatcher"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch SNU Watcher native validation"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-notifications"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveNativeNotificationRegistration"

[Code]
function StopExistingApp: Boolean;
var
  ResultCode: Integer;
  ExistingExe: String;
begin
  Result := True;
  ExistingExe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(ExistingExe) then
  begin
    Result := Exec(ExistingExe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if Result then Result := ResultCode = 0;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if StopExistingApp then Result := ''
  else Result := 'SNU Watcher did not stop successfully. Quit it and retry this installation.';
end;

function InitializeUninstall: Boolean;
begin
  Result := StopExistingApp;
  if not Result then MsgBox('SNU Watcher did not stop successfully. Quit it and retry uninstall.', mbError, MB_OK);
end;
