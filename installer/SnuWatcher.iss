; M2 unsigned feasibility candidate. Sign only in an approved release workflow.
#ifndef SourceDir
  #error SourceDir must be supplied explicitly; run scripts/Build-Native.ps1.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied explicitly; run scripts/Build-Native.ps1.
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

[Code]
// BEGIN INSTALLER RECOVERY
type
  TInstalledOwnerState = (iosAbsent, iosPresent, iosUnknown);
  // Inno 6.7.3 runs Pascal Script in Setup.e32 even in x64 install mode.
  // PROCESSENTRY32W is packed: nine DWORDs followed by 260 UTF-16 chars.
  TRecoveryProcessEntry = record
    Size, Usage, ProcessID, DefaultHeapID, ModuleID, Threads, ParentProcessID: LongWord;
    Priority: Integer;
    Flags: LongWord;
    ExeFile: array[0..259] of Char;
  end;

// BEGIN PROCESS INSPECTION BOUNDARY
// API contracts: learn.microsoft.com/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w
// Canonical names: learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
function WinSnapshot(Flags, ProcessID: LongWord): THandle;
  external 'CreateToolhelp32Snapshot@kernel32.dll stdcall';
function WinFirstProcess(Snapshot: THandle; var Entry: TRecoveryProcessEntry): Boolean;
  external 'Process32FirstW@kernel32.dll stdcall';
function WinNextProcess(Snapshot: THandle; var Entry: TRecoveryProcessEntry): Boolean;
  external 'Process32NextW@kernel32.dll stdcall';
function WinOpenProcess(Access: LongWord; Inherit: Boolean; ProcessID: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WinQueryImage(Process: THandle; Flags: LongWord; Buffer: String; var Characters: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function WinOpenFile(Path: String; Access, Sharing, Security, Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function WinFinalPath(FileHandle: THandle; Buffer: String; Characters, Flags: LongWord): LongWord;
  external 'GetFinalPathNameByHandleW@kernel32.dll stdcall';
function WinCloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function WinCurrentProcess: THandle;
  external 'GetCurrentProcess@kernel32.dll stdcall';
function WinIsWow64Process(Process: THandle; var Emulated: LongWord): Boolean;
  external 'IsWow64Process@kernel32.dll stdcall';

function SupportsProcessProbeAbi: Boolean;
var Entry: TRecoveryProcessEntry; Emulated: LongWord;
begin
  Result := SizeOf(Entry) = 556;
  if Result and IsWin64 then
  begin
    Emulated := 0;
    // Fail closed if a future compiler switches the script host to 64 bits.
    Result := WinIsWow64Process(WinCurrentProcess, Emulated) and (Emulated <> 0);
  end;
end;

function OpenProcessSnapshot: THandle;
begin Result := WinSnapshot($00000002, 0); end;
function FirstProcessEntry(Snapshot: THandle; var Entry: TRecoveryProcessEntry): Boolean;
begin Result := WinFirstProcess(Snapshot, Entry); end;
function NextProcessEntry(Snapshot: THandle; var Entry: TRecoveryProcessEntry): Boolean;
begin Result := WinNextProcess(Snapshot, Entry); end;
function LastProcessProbeError: LongWord;
begin Result := DLLGetLastError; end;
procedure CloseProcessProbeHandle(Handle: THandle);
begin
  if not WinCloseHandle(Handle) then RaiseException('Could not close the process inspection handle.');
end;

function TryCanonicalFilePath(const Path: String; var Canonical: String): Boolean;
var Handle: THandle; Buffer: String; Characters: LongWord;
begin
  Result := False;
  // Read attributes only; allow existing readers, writers and delete sharing.
  Handle := WinOpenFile(Path, 0, 7, 0, 3, $80, 0);
  if Handle = -1 then
  begin
    Log('Process inspection file open failed. Win32 error: ' + IntToStr(DLLGetLastError));
    Exit;
  end;
  try
    Buffer := StringOfChar(#0, 32768);
    Characters := WinFinalPath(Handle, Buffer, 32768, 0);
    if (Characters = 0) or (Characters >= 32768) then
    begin
      Log('Process inspection canonical path unavailable. Win32 error: ' + IntToStr(DLLGetLastError));
      Exit;
    end;
    SetLength(Buffer, Characters);
    Canonical := Buffer;
    Result := True;
  finally
    CloseProcessProbeHandle(Handle);
  end;
end;

function TryCanonicalProcessPath(ProcessID: LongWord; var Canonical: String): Boolean;
var Handle: THandle; Buffer: String; Characters: LongWord;
begin
  Result := False;
  Handle := WinOpenProcess($00001000, False, ProcessID);
  if Handle = 0 then Exit;
  try
    Buffer := StringOfChar(#0, 32768);
    Characters := 32768;
    if not WinQueryImage(Handle, 0, Buffer, Characters) then Exit;
    if (Characters = 0) or (Characters >= 32768) then Exit;
    SetLength(Buffer, Characters);
    Result := TryCanonicalFilePath(Buffer, Canonical);
  finally
    CloseProcessProbeHandle(Handle);
  end;
end;
// END PROCESS INSPECTION BOUNDARY

function TryEntryName(const Entry: TRecoveryProcessEntry; var Name: String): Boolean;
var I: Integer;
begin
  Result := False;
  Name := '';
  for I := 0 to 259 do
  begin
    if Entry.ExeFile[I] = #0 then
    begin
      Result := Name <> '';
      Exit;
    end;
    Name := Name + Entry.ExeFile[I];
  end;
end;

function InspectProcessSnapshot(Snapshot: THandle; const Target: String): TInstalledOwnerState;
var Entry: TRecoveryProcessEntry; Count: Integer; Name, Canonical: String; HaveEntry: Boolean;
begin
  Result := iosUnknown;
  Entry.Size := SizeOf(Entry);
  HaveEntry := FirstProcessEntry(Snapshot, Entry);
  Count := 0;
  while HaveEntry and (Count < 65536) do
  begin
    Count := Count + 1;
    if not TryEntryName(Entry, Name) then Exit;
    if CompareText(Name, '{#AppExe}') = 0 then
    begin
      // Inaccessible matching names are ambiguous; protected unrelated names
      // never need OpenProcess and therefore do not block every installation.
      if not TryCanonicalProcessPath(Entry.ProcessID, Canonical) then Exit;
      if CompareText(Canonical, Target) = 0 then
      begin
        Result := iosPresent;
        Exit;
      end;
    end;
    HaveEntry := NextProcessEntry(Snapshot, Entry);
  end;
  if not HaveEntry and (LastProcessProbeError = 18) then Result := iosAbsent;
end;

function InspectInstalledOwner(const ExistingExe: String): TInstalledOwnerState;
var Snapshot: THandle; Target: String;
begin
  Result := iosUnknown;
  try
    if not SupportsProcessProbeAbi then Exit;
    if not TryCanonicalFilePath(ExistingExe, Target) then Exit;
    Snapshot := OpenProcessSnapshot;
    if Snapshot = -1 then Exit;
    try
      Result := InspectProcessSnapshot(Snapshot, Target);
    finally
      CloseProcessProbeHandle(Snapshot);
    end;
  except
    Log('The installed application process state could not be verified.');
    Result := iosUnknown;
  end;
end;

// BEGIN INSTALLED APP BOUNDARY
function InstalledAppPath: String;
begin Result := ExpandConstant('{app}\{#AppExe}'); end;
function InstalledAppExists(const Path: String): Boolean;
begin Result := FileExists(Path); end;
function RunInstalledAppCommand(const Path, Command: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if (Command <> '--quit') and (Command <> '--unregister-notifications') then Exit;
  Result := Exec(Path, Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then Result := ResultCode = 0;
end;
// END INSTALLED APP BOUNDARY

function StopExistingApp(AllowRepair: Boolean): Boolean;
var ExistingExe: String;
begin
  ExistingExe := InstalledAppPath;
  if not InstalledAppExists(ExistingExe) then
  begin
    Result := AllowRepair;
    Exit;
  end;
  Result := RunInstalledAppCommand(ExistingExe, '--quit');
  if Result or not AllowRepair then Exit;
  // A partial self-contained installation cannot run --quit. Only a proven
  // absent owner permits replacement; ambiguity never authorizes termination.
  Result := InspectInstalledOwner(ExistingExe) = iosAbsent;
  if Result then Log('No installed application owner remains; repair is permitted.');
end;

function CanUninstallExistingApp: Boolean;
begin
  Result := StopExistingApp(False);
  if Result then Result := RunInstalledAppCommand(InstalledAppPath, '--unregister-notifications');
end;

function UninstallRecoveryMessage: String;
begin
  Result := 'SNU Watcher could not stop or remove its notification registration. ' +
    'Quit the app and retry. If it cannot start, repair SNU Watcher with the installer, then retry uninstall.';
end;
// END INSTALLER RECOVERY

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if StopExistingApp(True) then Result := ''
  else Result := 'SNU Watcher is running or its process state could not be verified. Quit it and retry this installation.';
end;

function InitializeUninstall: Boolean;
begin
  // Cleanup must succeed before any files are deleted. If the user subsequently
  // cancels uninstall, the next normal app launch registers notifications again.
  Result := CanUninstallExistingApp;
  if not Result then MsgBox(UninstallRecoveryMessage, mbError, MB_OK);
end;
