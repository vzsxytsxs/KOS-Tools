; KOS online installer (Inno Setup 6.7). Downloads the parts the user picks from the latest KOS release on GitHub (kos-release.ini
; lists each part's file, SHA-256, size and content id), checks every file's SHA-256, installs the apps to Program Files\KOS and the
; KOS scripts and tools to ProgramData\TheKOS. Run again, it updates: only parts whose content id changed are downloaded again, and
; a part that was unchecked is removed. Built by installer\build-release.ps1 (ISCC /DSetupVersion=...).
; kos-release.ini is signed (kos-release.ini.issig, ISSigTool with the key in KOS-private\keys; the public key is
; kos-release.ispublickey): only a release signed with it is installed. /SOURCE=<https://.../> takes the release from another
; https address; a test build (ISCC /DTestBuild) also takes an http address or a local folder, and skips the signature there.

#ifndef SetupVersion
  #define SetupVersion "0.0.0"
#endif
#define Repo "vzsxytsxs/KOS-Tools"

[ISSigKeys]
Name: "kos"; KeyFile: "kos-release.ispublickey"

[Setup]
AppId={{6F1D2B6C-4E0B-4C8E-9C2A-5B7E3D1A9F40}
AppName=KOS
AppVersion={#SetupVersion}
AppPublisher=KOS
AppPublisherURL=https://github.com/{#Repo}
AppSupportURL=https://github.com/{#Repo}/issues
AppUpdatesURL=https://github.com/{#Repo}/releases
DefaultDirName={commonpf64}\KOS
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
ArchiveExtraction=full
WizardStyle=modern
SetupIconFile=..\toolbox\src\KosToolbox\Assets\kos.ico
UninstallDisplayIcon={commonpf64}\KOS\toolbox\KosToolbox.exe
UninstallDisplayName=KOS
OutputDir=..\release
OutputBaseFilename=KOS-Setup
CloseApplications=no
RestartIfNeededByRun=no
SetupLogging=yes

[Types]
Name: "default"; Description: "Selected parts"; Flags: iscustom

; The order here is the order of Parts in [Code].
[Components]
Name: "toolbox"; Description: "KOS Toolbox"; Types: default
Name: "scripts"; Description: "KOS scripts (KOS Toolbox runs them)"; Types: default
Name: "tools"; Description: "KOS tools (CPU-Z, HWiNFO, y-cruncher and others)"
Name: "governor"; Description: "KOS Governor (process rules, gamemode; needs KOS Engine)"; Types: default
Name: "engine"; Description: "KOS Engine (tray, gamemode, dark mode for classic apps)"; Types: default
Name: "firewall"; Description: "KOS Firewall (needs KOS Engine)"; Types: default
Name: "equalizer"; Description: "KOS Equalizer"; Types: default

[Tasks]
Name: "signin"; Description: "Start KOS Engine when signing in to Windows"; Components: engine

; Created here so the uninstaller removes it once it's empty (the parts' folders are created and removed in [Code]).
[Dirs]
Name: "{app}"

[Run]
Filename: "{commonpf64}\KOS\toolbox\KosToolbox.exe"; Description: "Open KOS Toolbox"; Flags: postinstall nowait skipifsilent runasoriginaluser; Components: toolbox

[Code]
const
  PartCount = 7;
  InstallerKey = 'SOFTWARE\KOS\Installer';
  DefaultSource = 'https://github.com/{#Repo}/releases/latest/download/';
  RuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';

var
  Parts: array[0..PartCount - 1] of String;
  Source, Manifest: String;
  Download: array[0..PartCount - 1] of Boolean;
  NeedRuntime, IsKosWindows: Boolean;
  DownloadPage: TDownloadWizardPage;
  NotKosPage: TOutputMsgWizardPage;

{ ---------- the parts ---------- }

function PartDir(Part: String): String;
begin
  if (Part = 'scripts') or (Part = 'tools') then Result := ExpandConstant('{commonappdata}\TheKOS')
  else Result := ExpandConstant('{commonpf64}\KOS\') + Part;
end;

{ The exe that runs from a part's folder (closed before it's replaced). }
function PartExe(Part: String): String;
begin
  case Part of
    'toolbox': Result := 'KosToolbox.exe';
    'governor': Result := 'KosGovernor.exe';
    'engine': Result := 'KosEngine.exe';
    'firewall': Result := 'KosFirewall.exe';
    'equalizer': Result := 'KosEqualizer.exe';
  else Result := '';
  end;
end;

{ The part's zip in the release, and for the TheKOS parts their folder there: fixed here, never read from the manifest. }
function PartFile(Part: String): String;
begin
  case Part of
    'toolbox': Result := 'KosToolbox.zip';
    'scripts': Result := 'TheKOS-Setup.zip';
    'tools': Result := 'TheKOS-tools.zip';
    'governor': Result := 'KosGovernor.zip';
    'engine': Result := 'KosEngine.zip';
    'firewall': Result := 'KosFirewall.zip';
    'equalizer': Result := 'KosEqualizer.zip';
  end;
end;

function PartFolder(Part: String): String;
begin
  if Part = 'scripts' then Result := 'Setup' else Result := 'tools';
end;

function IsApp(Part: String): Boolean;
begin
  Result := (PartExe(Part) <> '') and (Part <> 'engine');
end;

function Info(Part, Key: String): String;
begin
  Result := GetIniString(Part, Key, '', Manifest);
end;

function InstalledId(Part: String): String;
begin
  if not RegQueryStringValue(HKLM64, InstallerKey, Part, Result) then Result := '';
end;

function IsInstalled(Part: String): Boolean;
begin
  Result := InstalledId(Part) <> '';
end;

function IsUrl(S: String): Boolean;
begin
  Result := (Pos('https://', Lowercase(S)) = 1) or (Pos('http://', Lowercase(S)) = 1);
end;

function IsHttps(S: String): Boolean;
begin
  Result := Pos('https://', Lowercase(S)) = 1;
end;

{ A part's SHA-256 from the manifest: exactly 64 hex digits, or the part isn't installed. }
function PartSha(Part: String): String;
var
  I: Integer;
begin
  Result := Lowercase(Info(Part, 'sha256'));
  if Length(Result) <> 64 then RaiseException(Part + ': the release lists no valid SHA-256.');
  for I := 1 to 64 do
    if Pos(Result[I], '0123456789abcdef') = 0 then RaiseException(Part + ': the release lists no valid SHA-256.');
end;

function SizeText(Bytes: String): String;
var
  N: Int64;
begin
  N := StrToInt64Def(Bytes, 0);
  if N >= 1048576 then Result := IntToStr(Integer(N div 1048576)) + ' MB'
  else Result := IntToStr(Integer((N + 1023) div 1024)) + ' KB';
end;

{ ---------- the release ---------- }

// A release file into the temporary folder: downloaded (checked against its SHA-256 by the download) or copied from a local folder and checked here.
procedure FetchNow(Name, Sha: String);
var
  Target: String;
begin
  Target := ExpandConstant('{tmp}\') + Name;
  if IsUrl(Source) then
    DownloadTemporaryFile(Source + Name, Name, Sha, nil)
  else begin
    if Sha = '' then RaiseException(Name + ': no SHA-256.'); // '-': the test manifest, unsigned
    if not CopyFile(AddBackslash(Source) + Name, Target, False) then RaiseException('Not found: ' + AddBackslash(Source) + Name);
    if (Sha <> '-') and (Lowercase(GetSHA256OfFile(Target)) <> Lowercase(Sha)) then RaiseException(Name + ' doesn''t match its SHA-256.');
  end;
end;

function RuntimeInstalled: Boolean;
var
  Find: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), Find);
  if Result then FindClose(Find);
end;

function InitializeSetup: Boolean;
begin
  Parts[0] := 'toolbox'; Parts[1] := 'scripts'; Parts[2] := 'tools'; Parts[3] := 'governor';
  Parts[4] := 'engine'; Parts[5] := 'firewall'; Parts[6] := 'equalizer';
  Source := ExpandConstant('{param:SOURCE|' + DefaultSource + '}');
  if IsUrl(Source) and (Copy(Source, Length(Source), 1) <> '/') then Source := Source + '/';
  Manifest := ExpandConstant('{tmp}\kos-release.ini');
  // A KOS Windows install has its profile in the registry, or the folder its setup leaves (older installs).
  IsKosWindows := RegValueExists(HKLM64, 'SOFTWARE\TheKOS', 'Profile') or DirExists(ExpandConstant('{commonappdata}\TheKOS\bin'));
  try
    #ifndef TestBuild
    if not IsHttps(Source) then RaiseException('Only an https:// source is accepted.');
    #endif
    // The manifest is signed: its SHA-256s are what every part is checked against.
    if IsHttps(Source) then
      DownloadTemporaryFileWithISSigVerify(Source + 'kos-release.ini', '', 'kos-release.ini', nil, nil)
    else if IsUrl(Source) then
      DownloadTemporaryFile(Source + 'kos-release.ini', 'kos-release.ini', '', nil)
    else
      FetchNow('kos-release.ini', '-');
    Result := Info('release', 'version') <> '';
    if not Result then RaiseException('kos-release.ini has no release version.');
  except
    SuppressibleMsgBox('The KOS release couldn''t be read from ' + Source + ':' + #13#10 + GetExceptionMessage, mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ ---------- wizard ---------- }

procedure ComponentsClicked(Sender: TObject);
var
  List: TNewCheckListBox;
begin
  List := WizardForm.ComponentsList;
  // KOS Governor and KOS Firewall need KOS Engine (KOS Firewall: to be turned off when it's removed); KOS Toolbox the KOS scripts.
  if (List.Checked[3] or List.Checked[5]) and not List.Checked[4] then List.Checked[4] := True;
  if List.Checked[0] and not List.Checked[1] then List.Checked[1] := True;
end;

procedure InitializeWizard;
begin
  NotKosPage := CreateOutputMsgPage(wpWelcome, 'This isn''t a KOS Windows install',
    'KOS was made for the KOS build of Windows 11.',
    'This PC runs a regular Windows. KOS can be installed, but some parts may not work correctly: the KOS scripts and the ' +
    'Windows page expect the changes the KOS build makes, and the KOS state check will list most of them as missing.' + #13#10#13#10 +
    'Click Next to install anyway.');
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading the selected parts of KOS ' + Info('release', 'version') + '.', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  WizardForm.ComponentsList.OnClickCheck := @ComponentsClicked;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = NotKosPage.ID) and IsKosWindows;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  I: Integer;
  State: String;
begin
  if CurPageID <> wpSelectComponents then Exit;
  // Inno only knows its own size, not the downloads': each part's size is in its row instead.
  WizardForm.ComponentsDiskSpaceLabel.Visible := False;
  for I := 0 to PartCount - 1 do begin
    if not IsInstalled(Parts[I]) then State := ''
    else if InstalledId(Parts[I]) = Info(Parts[I], 'id') then State := 'installed, up to date'
    else State := 'update available';
    WizardForm.ComponentsList.ItemSubItem[I] := SizeText(Info(Parts[I], 'size'));
    if State <> '' then WizardForm.ComponentsList.ItemCaption[I] := WizardForm.ComponentsList.ItemCaption[I] + ' (' + State + ')';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  I, Count: Integer;
  Part: String;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;
  Count := 0;
  NeedRuntime := False;
  DownloadPage.Clear;
  try
  for I := 0 to PartCount - 1 do begin
    Part := Parts[I];
    Download[I] := WizardIsComponentSelected(Part) and
      ((InstalledId(Part) <> Info(Part, 'id')) or ((PartExe(Part) <> '') and not FileExists(AddBackslash(PartDir(Part)) + PartExe(Part))));
    if WizardIsComponentSelected(Part) and IsApp(Part) and not RuntimeInstalled then NeedRuntime := True;
    if Download[I] then begin
      if IsUrl(Source) then DownloadPage.Add(Source + PartFile(Part), PartFile(Part), PartSha(Part));
      Count := Count + 1;
    end;
  end;
  except
    SuppressibleMsgBox(GetExceptionMessage, mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  if NeedRuntime then begin
    DownloadPage.Add(RuntimeUrl, 'windowsdesktop-runtime.exe', '');
    Count := Count + 1;
  end;
  if (Count = 0) or not IsUrl(Source) then begin
    // A local source: copied and checked here.
    try
      for I := 0 to PartCount - 1 do
        if Download[I] then FetchNow(PartFile(Parts[I]), PartSha(Parts[I]));
    except
      SuppressibleMsgBox(GetExceptionMessage, mbCriticalError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
    if not NeedRuntime then Exit;
    DownloadPage.Clear;
    DownloadPage.Add(RuntimeUrl, 'windowsdesktop-runtime.exe', '');
  end;
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox(AddPeriod(Format('%s: %s', [DownloadPage.LastBaseNameOrUrl, GetExceptionMessage])), mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

{ ---------- install ---------- }

function Run(Exe, Params: String): Integer;
begin
  if not Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then Result := -1;
  Log(Format('%s %s -> %d', [Exe, Params, Result]));
end;

procedure Status(Text: String);
begin
  WizardForm.StatusLabel.Caption := Text;
  WizardForm.FilenameLabel.Caption := '';
end;

function OpenEvent(Access: Cardinal; Inherit: Boolean; Name: String): THandle; external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): Boolean; external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean; external 'CloseHandle@kernel32.dll stdcall';

{ KOS Engine's stop event (as its tray's Exit). Set here, not through "KosEngine.exe --setup stop": an engine from before the
  installer doesn't know that command and would start as a second engine and never return. }
procedure StopEngine;
var
  Handle: THandle;
begin
  Handle := OpenEvent($0002, False, 'Local\KosGovernor.Stop'); // EVENT_MODIFY_STATE
  if Handle <> 0 then begin
    SetEvent(Handle);
    CloseHandle(Handle);
    Sleep(3000);
  end;
end;

{ Closes the KOS apps and stops KOS Engine (forced only if it doesn't end), so their files can be replaced. }
procedure CloseKos;
var
  I: Integer;
begin
  StopEngine;
  for I := 0 to PartCount - 1 do
    if IsApp(Parts[I]) then Run(ExpandConstant('{sys}\taskkill.exe'), '/IM ' + PartExe(Parts[I]));
  Sleep(1500);
  for I := 0 to PartCount - 1 do
    if PartExe(Parts[I]) <> '' then Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + PartExe(Parts[I]));
end;

{ An app's folder emptied for the new files. A DLL in use (KOS Equalizer's audio effect inside the audio service, KOS Shell's
  inside other apps) can't be deleted: it's renamed and removed at the next restart. }
procedure ClearAppDir(Dir: String);
var
  Find: TFindRec;
  Old: String;
begin
  if FindFirst(AddBackslash(Dir) + '*', Find) then
  try
    repeat
      if (Find.Name = '.') or (Find.Name = '..') then Continue;
      if Find.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then DelTree(AddBackslash(Dir) + Find.Name, True, True, True)
      else if not DeleteFile(AddBackslash(Dir) + Find.Name) then begin
        Old := AddBackslash(Dir) + Find.Name + '.' + GetDateTimeString('yyyymmddhhnnss', #0, #0) + '.old';
        if RenameFile(AddBackslash(Dir) + Find.Name, Old) then RestartReplace(Old, '')
        else RaiseException('In use and can''t be replaced: ' + AddBackslash(Dir) + Find.Name);
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

function ExtractProgress(const ArchiveName, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  WizardForm.FilenameLabel.Caption := FileName;
  if ProgressMax > 0 then WizardForm.ProgressGauge.Position := Integer(Progress * 1000 div ProgressMax);
  Result := True;
end;

procedure InstallRuntime;
var
  Exe, Quoted: String;
  Code: Integer;
begin
  Exe := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');
  Quoted := Exe;
  StringChangeEx(Quoted, '''', '''''', True); // inside a PowerShell single-quoted string
  Status('Checking the .NET Desktop Runtime''s signature...');
  // The runtime link always gives the latest 8.0 build, so there's no fixed SHA-256: its Microsoft signature is checked instead.
  if Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "$s = Get-AuthenticodeSignature -LiteralPath ''' + Quoted + '''; ' +
      'if ($s.Status -eq ''Valid'' -and $s.SignerCertificate.Subject -match ''O=Microsoft Corporation'') { exit 0 } else { exit 1 }"') <> 0 then
    RaiseException('The downloaded .NET Desktop Runtime isn''t signed by Microsoft.');
  Status('Installing the .NET 8 Desktop Runtime...');
  Code := Run(Exe, '/install /quiet /norestart');
  if (Code <> 0) and (Code <> 3010) and (Code <> 1641) then RaiseException(Format('The .NET Desktop Runtime setup failed (exit code %d).', [Code]));
end;

procedure RemovePart(Part: String);
begin
  if PartExe(Part) <> '' then DelTree(PartDir(Part), True, True, True)
  else if not IsKosWindows then DelTree(PartDir(Part) + '\' + PartFolder(Part), True, True, True);
  RegDeleteValue(HKLM64, InstallerKey, Part);
end;

function Powershell(Command: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -Command "' + Command + '"');
end;

{ ProgramData\TheKOS, when it was there before: refused if it's a link or not owned by SYSTEM or Administrators (on a regular
  Windows any user can create it first, and the toolbox runs the scripts in it as administrator). }
procedure CheckTheKos;
var
  Dir: String;
begin
  Dir := ExpandConstant('{commonappdata}\TheKOS');
  if not DirExists(Dir) then Exit;
  StringChangeEx(Dir, '''', '''''', True);
  if Powershell('$i = Get-Item -LiteralPath ''' + Dir + ''' -Force; if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 2 }; ' +
      '$o = (Get-Acl -LiteralPath ''' + Dir + ''').GetOwner([Security.Principal.SecurityIdentifier]).Value; ' +
      'if ($o -eq ''S-1-5-32-544'' -or $o -eq ''S-1-5-18'') { exit 0 } else { exit 1 }') <> 0 then
    RaiseException(ExpandConstant('{commonappdata}\TheKOS') + ' is a link or isn''t owned by the Administrators group or SYSTEM, so the KOS scripts aren''t put there. Remove or fix it and run the setup again.');
end;

{ KOS Engine's setup commands: only an engine this installer put there (an older one doesn't know them and would start as an
  engine and never return). }
function EngineCommand(Params: String): Integer;
var
  Engine: String;
begin
  Engine := ExpandConstant('{commonpf64}\KOS\engine\KosEngine.exe');
  if IsInstalled('engine') and FileExists(Engine) then Result := Run(Engine, Params) else Result := -2;
end;

procedure InstallParts;
var
  I: Integer;
  Part, Engine: String;
  Changing: Boolean;
begin
  Changing := NeedRuntime;
  for I := 0 to PartCount - 1 do
    if Download[I] or (IsInstalled(Parts[I]) and not WizardIsComponentSelected(Parts[I])) then Changing := True;
  if Changing then begin
    Status('Closing the KOS apps...');
    CloseKos;
  end;
  if NeedRuntime then InstallRuntime;
  // KOS Firewall unchecked: turned off first (Windows Firewall back on), while KOS Engine is still there to do it.
  if IsInstalled('firewall') and not WizardIsComponentSelected('firewall') and (EngineCommand('--setup firewall-off') <> 0) then
    RaiseException('KOS Firewall couldn''t be turned off, so it isn''t removed. Turn it off in KOS Firewall and run the setup again.');
  if Download[1] or Download[2] then CheckTheKos;
  for I := 0 to PartCount - 1 do begin
    Part := Parts[I];
    if IsInstalled(Part) and not WizardIsComponentSelected(Part) then begin
      Status('Removing ' + Part + '...');
      // KOS Equalizer stays if it couldn't be taken off every device: removing its effect DLL would leave those devices silent.
      if (Part = 'equalizer') and (Run(AddBackslash(PartDir(Part)) + PartExe(Part), '--uninstall') <> 0) then
        Log('KOS Equalizer couldn''t be turned off on every device: it stays installed.')
      else begin
        if Part = 'engine' then EngineCommand('--setup sign-in off');
        RemovePart(Part);
      end;
    end;
    if not Download[I] then Continue;
    Status('Installing ' + Info(Part, 'name') + '...');
    ForceDirectories(PartDir(Part));
    if PartExe(Part) <> '' then ClearAppDir(PartDir(Part));
    ExtractArchive(ExpandConstant('{tmp}\') + PartFile(Part), PartDir(Part), '', True, @ExtractProgress);
    if (PartExe(Part) <> '') and not FileExists(AddBackslash(PartDir(Part)) + PartExe(Part)) then
      RaiseException(Info(Part, 'name') + ' wasn''t installed: ' + PartExe(Part) + ' is missing after extracting.');
    RegWriteStringValue(HKLM64, InstallerKey, Part, Info(Part, 'id'));
  end;
  Engine := ExpandConstant('{commonpf64}\KOS\engine\KosEngine.exe');
  if WizardIsComponentSelected('engine') and FileExists(Engine) then begin
    Status('Starting KOS Engine...');
    if WizardIsTaskSelected('signin') then begin
      if Run(Engine, '--setup sign-in on') <> 0 then Log('The sign-in task couldn''t be created.');
      Run(ExpandConstant('{sys}\schtasks.exe'), '/Run /TN "KOS Engine"');
    end else begin
      Run(Engine, '--setup sign-in off');
      ExecAsOriginalUser(Engine, '', '', SW_SHOWNORMAL, ewNoWait, I);
    end;
  end;
  RegWriteStringValue(HKLM64, InstallerKey, 'Release', Info('release', 'version'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    try
      InstallParts;
    except
      Log('Install failed: ' + GetExceptionMessage);
      RaiseException(GetExceptionMessage);
    end;
end;

{ ---------- uninstall ---------- }

procedure DelIf(Path: String);
begin
  if DirExists(Path) then DelTree(Path, True, True, True);
end;

function InstalledReg(Part: String): Boolean;
var
  Id: String;
begin
  Result := RegQueryStringValue(HKLM64, InstallerKey, Part, Id) and (Id <> '');
end;

{ KOS Firewall off before anything is removed (Windows Firewall back on); if that fails, nothing is uninstalled: without KOS
  Firewall and its config nothing could remove its filters, which hold from boot. }
function InitializeUninstall: Boolean;
var
  Engine, Config: String;
  Text: AnsiString;
  Code: Integer;
begin
  Result := True;
  Engine := ExpandConstant('{commonpf64}\KOS\engine\KosEngine.exe');
  Config := ExpandConstant('{commonappdata}\TheKOS\Firewall\config.json');
  if InstalledReg('engine') and FileExists(Engine) then begin
    if not Exec(Engine, '--setup firewall-off', '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := -1;
  end else if LoadStringFromFile(Config, Text) and (Pos('"Enabled": true', Copy(Text, 1, 64)) > 0) then
    Code := -3 // on, and no KOS Engine to turn it off
  else Code := 0;
  if Code <> 0 then begin
    SuppressibleMsgBox(Format('KOS Firewall couldn''t be turned off (%d), so KOS wasn''t uninstalled: its filters would stay with nothing to remove them. ' +
      'Turn it off in KOS Firewall (or set Windows > Security > Firewall to Enabled in KOS Toolbox) and uninstall again.', [Code]), mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Kos, Engine, Equalizer, Data: String;
  Code: Integer;
  KeepEqualizer: Boolean;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Kos := ExpandConstant('{commonpf64}\KOS');
  Engine := Kos + '\engine\KosEngine.exe';
  Equalizer := Kos + '\equalizer\KosEqualizer.exe';
  Data := ExpandConstant('{commonappdata}\TheKOS');
  StopEngine; // KOS Firewall is off already (InitializeUninstall)
  if InstalledReg('engine') and FileExists(Engine) then Exec(Engine, '--setup sign-in off', '', SW_HIDE, ewWaitUntilTerminated, Code);
  KeepEqualizer := FileExists(Equalizer) and not (Exec(Equalizer, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0));
  if KeepEqualizer then Log('KOS Equalizer couldn''t be turned off on every device: its folder stays (removing its effect would leave those devices silent).');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM KosToolbox.exe /IM KosGovernor.exe /IM KosFirewall.exe /IM KosEqualizer.exe /IM KosEngine.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1000);
  DelIf(Kos + '\toolbox'); DelIf(Kos + '\governor'); DelIf(Kos + '\engine'); DelIf(Kos + '\firewall');
  if not KeepEqualizer then DelIf(Kos + '\equalizer');
  RemoveDir(Kos);
  // KOS's data goes too. On a KOS Windows install, what its setup put in TheKOS (scripts, tools, bin, the baseline) stays.
  if RegValueExists(HKLM64, 'SOFTWARE\TheKOS', 'Profile') or DirExists(Data + '\bin') then begin
    if not KeepEqualizer then DelIf(Data + '\Audio');
    DelIf(Data + '\Firewall'); DelIf(Data + '\Governor'); DelIf(Data + '\Logs'); DelIf(Data + '\Toolbox');
  end else if KeepEqualizer then begin
    DelIf(Data + '\Setup'); DelIf(Data + '\tools'); DelIf(Data + '\Firewall'); DelIf(Data + '\Governor'); DelIf(Data + '\Logs'); DelIf(Data + '\Toolbox');
  end else
    DelIf(Data);
  DelIf(ExpandConstant('{localappdata}\KOS'));
  RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\KOS');
end;
