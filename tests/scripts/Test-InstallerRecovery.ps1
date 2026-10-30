# Execute the installer policy as Pascal Script without installing an application.
# InitializeSetup always returns False; all output stays in a fresh artifact folder.
[CmdletBinding()]
param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $workspace = Split-Path (Split-Path $repoRoot -Parent) -Parent
    $IsccPath = Join-Path $workspace 'work/toolchain/InnoSetup/ISCC.exe'
}
if (!(Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Supply the Inno Setup compiler with -IsccPath.' }
$source = Get-Content -LiteralPath (Join-Path $repoRoot 'installer/SnuWatcher.iss') -Raw
function Get-MarkedBlock([string]$Text, [string]$Name) {
    $start = '// BEGIN ' + $Name
    $end = '// END ' + $Name
    $first = $Text.IndexOf($start, [StringComparison]::Ordinal)
    $last = $Text.IndexOf($end, [StringComparison]::Ordinal)
    if ($first -lt 0 -or $last -le $first -or $Text.LastIndexOf($start, [StringComparison]::Ordinal) -ne $first) {
        throw "Missing or duplicated production block: $Name"
    }
    $Text.Substring($first, $last + $end.Length - $first)
}
$policy = Get-MarkedBlock $source 'INSTALLER RECOVERY'
$native = Get-MarkedBlock $policy 'PROCESS INSPECTION BOUNDARY'
$appBoundary = Get-MarkedBlock $policy 'INSTALLED APP BOUNDARY'
$runRoot = Join-Path $repoRoot ('artifacts/installer-recovery-tests/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot

$fakeNative = @'
var
  FakeOwnerState: Integer;
  FakeSnapshotFails: Boolean;
  FakeNextFails: Boolean;
  FakeName: String;
  FakeLastError: LongWord;
  FakeClosedHandles: Integer;
  FakeAbiSupported: Boolean;
  FakeEntries: Integer;
  FakeIndex: Integer;
  FakeFirstFails: Boolean;
  FakeCloseFails: Boolean;

function SupportsProcessProbeAbi: Boolean;
begin Result := FakeAbiSupported; end;
function OpenProcessSnapshot: THandle;
begin
  if FakeSnapshotFails then Result := -1 else Result := 40;
end;
procedure SetFakeEntry(var Entry: TRecoveryProcessEntry);
var I: Integer;
begin
  Entry.ProcessID := 99;
  for I := 1 to Length(FakeName) do Entry.ExeFile[I - 1] := FakeName[I];
  if Length(FakeName) < 260 then Entry.ExeFile[Length(FakeName)] := #0;
end;
function FirstProcessEntry(Snapshot: THandle; var Entry: TRecoveryProcessEntry): Boolean;
begin
  FakeIndex := 1;
  Result := (FakeEntries > 0) and not FakeFirstFails;
  if Result then SetFakeEntry(Entry)
  else if FakeFirstFails then FakeLastError := 5 else FakeLastError := 18;
end;
function NextProcessEntry(Snapshot: THandle; var Entry: TRecoveryProcessEntry): Boolean;
begin
  FakeIndex := FakeIndex + 1;
  Result := (FakeIndex <= FakeEntries) and not FakeNextFails;
  if Result then SetFakeEntry(Entry)
  else if FakeNextFails then FakeLastError := 5 else FakeLastError := 18;
end;
function LastProcessProbeError: LongWord;
begin Result := FakeLastError; end;
procedure CloseProcessProbeHandle(Handle: THandle);
begin
  FakeClosedHandles := FakeClosedHandles + 1;
  if FakeCloseFails then RaiseException('Injected handle-close failure');
end;
function TryCanonicalFilePath(const Path: String; var Canonical: String): Boolean;
begin
  Result := FakeOwnerState <> 3;
  Canonical := 'C:\fixture\SnuWatcher.Desktop.exe';
end;
function TryCanonicalProcessPath(ProcessID: LongWord; var Canonical: String): Boolean;
begin
  Result := FakeOwnerState <> 2;
  if FakeOwnerState = 1 then Canonical := 'c:\FIXTURE\SNUWATCHER.DESKTOP.EXE'
  else Canonical := 'C:\another-copy\SnuWatcher.Desktop.exe';
end;
'@
$fakeApp = @'
var
  FakeExists: Boolean;
  FakeQuit: Boolean;
  FakeUnregister: Boolean;
  FakeCommands: String;
function InstalledAppPath: String;
begin Result := 'C:\fixture\SnuWatcher.Desktop.exe'; end;
function InstalledAppExists(const Path: String): Boolean;
begin Result := FakeExists; end;
function RunInstalledAppCommand(const Path, Command: String): Boolean;
begin
  FakeCommands := FakeCommands + Command + ';';
  if Command = '--quit' then Result := FakeQuit
  else if Command = '--unregister-notifications' then Result := FakeUnregister
  else Result := False;
end;
'@
$fakePolicy = $policy.Replace($native, $fakeNative).Replace($appBoundary, $fakeApp)
$assertions = @'
var Checks: Integer;
procedure AssertCondition(Condition: Boolean; const Description: String);
begin
  if not Condition then RaiseException(Description);
  Checks := Checks + 1;
end;
procedure ResetCase;
begin
  FakeOwnerState := 0; FakeSnapshotFails := False; FakeNextFails := False;
  FakeName := 'SnuWatcher.Desktop.exe'; FakeLastError := 18;
  FakeClosedHandles := 0; FakeAbiSupported := True; FakeEntries := 0;
  FakeFirstFails := False; FakeCloseFails := False;
  FakeExists := True; FakeQuit := False; FakeUnregister := True; FakeCommands := '';
end;
procedure RunPolicyCases;
begin
  ResetCase;
  AssertCondition(StopExistingApp(True), 'An unstartable app with no owner must be repairable');
  AssertCondition(FakeClosedHandles = 1, 'An empty snapshot must be closed');
  AssertCondition(FakeCommands = '--quit;', 'Repair must never unregister notifications');
  ResetCase; FakeQuit := True;
  AssertCondition(StopExistingApp(True), 'A healthy app must retain graceful quit');
  AssertCondition(FakeClosedHandles = 0, 'A successful quit must not use the recovery fallback');
  ResetCase; FakeExists := False;
  AssertCondition(StopExistingApp(True), 'A fresh installation must be permitted');
  AssertCondition(FakeCommands = '', 'A missing executable must never be launched');
  ResetCase; FakeEntries := 1; FakeOwnerState := 1;
  AssertCondition(not StopExistingApp(True), 'A matching owner must block repair');
  AssertCondition(FakeClosedHandles = 1, 'A matching-owner snapshot must close');
  ResetCase; FakeEntries := 1;
  AssertCondition(StopExistingApp(True), 'An accessible different copy must not block repair');
  ResetCase; FakeEntries := 1; FakeOwnerState := 2;
  AssertCondition(not StopExistingApp(True), 'An inaccessible matching name must block repair');
  ResetCase; FakeOwnerState := 3;
  AssertCondition(not StopExistingApp(True), 'A target canonicalization failure must block repair');
  ResetCase; FakeSnapshotFails := True;
  AssertCondition(not StopExistingApp(True), 'Snapshot failure must block repair');
  AssertCondition(FakeClosedHandles = 0, 'An invalid snapshot must not be closed as a live handle');
  ResetCase; FakeFirstFails := True;
  AssertCondition(not StopExistingApp(True), 'Unexpected first-entry failure must block repair');
  ResetCase; FakeCloseFails := True;
  AssertCondition(not StopExistingApp(True), 'Handle-close exceptions must remain fail-closed');
  ResetCase; FakeAbiSupported := False;
  AssertCondition(not StopExistingApp(True), 'An unsupported ABI must block repair');
  ResetCase; FakeEntries := 1; FakeNextFails := True;
  AssertCondition(not StopExistingApp(True), 'Unexpected enumeration failure must block repair');
  ResetCase; FakeEntries := 65537; FakeName := 'other.exe';
  AssertCondition(not StopExistingApp(True), 'Enumeration must be bounded');
  ResetCase; FakeEntries := 1; FakeName := 'SnuWatcher.Desktop.exe.other'; FakeOwnerState := 2;
  AssertCondition(StopExistingApp(True), 'A basename prefix is not the app');
  ResetCase; FakeEntries := 1; FakeName := 'SNUWATCHER.DESKTOP.EXE'; FakeOwnerState := 1;
  AssertCondition(not StopExistingApp(True), 'Name and canonical-path comparisons must ignore case');
  ResetCase; FakeEntries := 1; FakeName := StringOfChar('X', 260);
  AssertCondition(not StopExistingApp(True), 'An unterminated snapshot name must remain unknown');
  ResetCase;
  AssertCondition(not CanUninstallExistingApp, 'Broken quit must never bypass cleanup during uninstall');
  AssertCondition(FakeCommands = '--quit;', 'Failed quit must stop before unregister');
  ResetCase; FakeExists := False;
  AssertCondition(not CanUninstallExistingApp, 'Missing executable requires repair before uninstall');
  ResetCase; FakeQuit := True; FakeUnregister := False;
  AssertCondition(not CanUninstallExistingApp, 'Failed unregister must block uninstall');
  AssertCondition(FakeCommands = '--quit;--unregister-notifications;', 'Cleanup order must be quit then unregister');
  ResetCase; FakeQuit := True; FakeUnregister := True;
  AssertCondition(CanUninstallExistingApp, 'Uninstall requires both successful CLI commands');
  AssertCondition(Pos('repair', Lowercase(UninstallRecoveryMessage)) > 0, 'Uninstall guidance must explain repair');
end;
'@

function Invoke-PascalFixture([string]$Name, [string]$Code, [string]$Tests, [string]$AppExe = 'SnuWatcher.Desktop.exe') {
    $fixtureDirectory = Join-Path $runRoot $Name
    $null = New-Item -ItemType Directory -Path $fixtureDirectory
    $resultPath = Join-Path $fixtureDirectory 'result.txt'
    $header = @'
[Setup]
AppName=SNU Watcher installer recovery self-test
AppVersion=0.0
DefaultDirName={tmp}\SnuWatcherRecoveryFixture
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
DisableStartupPrompt=yes
OutputBaseFilename=RecoveryFixture
Compression=none
[Code]
'@
    $footer = @'
function InitializeSetup: Boolean;
var Outcome: String;
begin
  try
    RunPolicyCases;
    Outcome := 'PASS ' + IntToStr(Checks);
  except
    Outcome := 'FAIL ' + GetExceptionMessage;
  end;
  SaveStringToFile(ExpandConstant('{param:ResultPath}'), Outcome, False);
  Result := False;
end;
'@
    $scriptPath = Join-Path $fixtureDirectory 'fixture.iss'
    $definition = '#define AppExe "' + $AppExe + '"'
    [IO.File]::WriteAllText($scriptPath, ($definition + [Environment]::NewLine + $header + [Environment]::NewLine + $Code + [Environment]::NewLine + $Tests + [Environment]::NewLine + $footer))
    $ErrorActionPreference = 'Continue'
    $compileOutput = @(& $IsccPath '/Q' ('/O' + $fixtureDirectory) $scriptPath 2>&1)
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { throw ($compileOutput -join [Environment]::NewLine) }
    $exe = Join-Path $fixtureDirectory 'RecoveryFixture.exe'
    $otherCopy = Join-Path $fixtureDirectory 'other-copy'
    $null = New-Item -ItemType Directory -Path $otherCopy
    $otherFile = Join-Path $otherCopy 'RecoveryFixture.exe'
    Set-Content -LiteralPath $otherFile -Value 'Read-only probe target; never execute.'
    $aliasPath = Join-Path $runRoot ($Name + '-alias')
    $null = New-Item -ItemType Junction -Path $aliasPath -Target $fixtureDirectory
    $aliasFile = Join-Path $aliasPath 'RecoveryFixture.exe'
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/ResultPath="' + $resultPath + '"'),
        ('/OtherCopy="' + $otherFile + '"'), ('/AliasPath="' + $aliasFile + '"'),
        ('/LOG="' + (Join-Path $fixtureDirectory 'fixture.log') + '"'))
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(30000)) { throw 'The no-install Pascal fixture exceeded its deadline; inspect before retrying.' }
    } finally { $process.Dispose() }
    if (!(Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw 'The no-install fixture did not return evidence.' }
    $result = (Get-Content -LiteralPath $resultPath -Raw).Trim()
    if ($result -notmatch '^PASS [0-9]+$') { throw $result }
    Write-Output ($Name + ': ' + $result)
}

Invoke-PascalFixture 'policy' $fakePolicy $assertions

$nativeAssertions = @'
var Checks: Integer;
procedure AssertCondition(Condition: Boolean; const Description: String);
begin
  if not Condition then RaiseException(Description);
  Checks := Checks + 1;
end;
procedure RunPolicyCases;
var Target, Canonical, AliasCanonical, OtherCopy, AliasPath: String;
begin
  Target := ExpandConstant('{srcexe}');
  OtherCopy := ExpandConstant('{param:OtherCopy}');
  AliasPath := ExpandConstant('{param:AliasPath}');
  AssertCondition(SupportsProcessProbeAbi, 'Pinned Inno must use the checked 32-bit ToolHelp ABI');
  AssertCondition(TryCanonicalFilePath(Target, Canonical), 'A readable executable must canonicalize');
  if TryCanonicalFilePath(AliasPath, AliasCanonical) then
  begin
    AssertCondition(CompareText(Canonical, AliasCanonical) = 0, 'Readable aliases must resolve to the same canonical path');
    AssertCondition(InspectInstalledOwner(AliasPath) = iosPresent, 'A readable alias must identify its running owner');
  end
  else
  begin
    // Inno enables Windows Redirection Guard. An untrusted junction may be
    // rejected with ERROR_UNTRUSTED_MOUNT_POINT; do not weaken that mitigation.
    AssertCondition(LastProcessProbeError = 448, 'Only the recorded mount-point protection may reject this existing alias');
    AssertCondition(InspectInstalledOwner(AliasPath) = iosUnknown, 'An unreadable alias must remain unknown, never absent');
  end;
  AssertCondition(not TryCanonicalFilePath(Target + '.missing', Canonical), 'A missing target must fail closed');
  AssertCondition(not TryCanonicalProcessPath($FFFFFFFE, Canonical), 'A nonexistent process must fail closed');
  AssertCondition(InspectInstalledOwner(Target) = iosPresent, 'The running fixture loader must be detected as an owner');
  AssertCondition(InspectInstalledOwner(OtherCopy) = iosAbsent, 'A same-name different executable must not match');
  AssertCondition(not RunInstalledAppCommand(Target, '--arbitrary'), 'Unsupported commands must never execute');
end;
'@
Invoke-PascalFixture 'native-probe' $policy $nativeAssertions 'RecoveryFixture.exe'

# Compile the real production script against a tiny throwaway source tree.
# This binary is never executed; only no-install fixtures above are run.
$compileSource = Join-Path $runRoot 'compile-source'
$compileOutput = Join-Path $runRoot 'production-compile'
$null = New-Item -ItemType Directory -Path $compileSource, $compileOutput
Set-Content -LiteralPath (Join-Path $compileSource 'SnuWatcher.Desktop.exe') -Value 'Compile-only fixture; never execute.'
$ErrorActionPreference = 'Continue'
$compilerOutput = @(& $IsccPath '/Q' ('/DSourceDir=' + $compileSource) ('/DOutputDir=' + $compileOutput) (Join-Path $repoRoot 'installer/SnuWatcher.iss') 2>&1)
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -ne 0) { throw ($compilerOutput -join [Environment]::NewLine) }
Write-Output 'Production Inno script compilation passed. No application was installed.'
