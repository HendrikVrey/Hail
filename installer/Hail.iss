; Hail - Inno Setup script
;
; Per-user, no elevation, no UAC prompt. Everything this writes lives under
; HKEY_CURRENT_USER and %LOCALAPPDATA%, and nothing here affects another account.
;
; Build:  iscc installer\Hail.iss /DAppVersion=1.2.3 /DNumericVersion=1.2.3
; Expects a published folder for each architecture:
;   publish\win-x64\Hail.exe
;   publish\win-arm64\Hail.exe
;
; Hail is resident: it runs from sign-in to sign-out with its files open. So before files are
; replaced or removed, the running Hail is asked to quit through its own "--quit" (the same
; request a second start makes), and the installer waits for it to let go of its session's
; single-instance mutex. That is gentler than the Restart Manager, which would have to close a
; program whose only windows are hidden; it stays switched on as the fallback.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; VersionInfoVersion must be strictly numeric (x.y.z[.w]); AppVersion may carry a pre-release
; suffix. The workflow passes both.
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif

#define AppName        "Hail"
#define AppPublisher   "Hendrik Vrey"
#define AppUrl         "https://github.com/HendrikVrey/Hail"
#define AppExeName     "Hail.exe"
#define AppGuid        "5B0E2F7A-3C91-4D6B-9A2E-8F14C7D3B6A5"
#define AppMutexName   "Local\Hail.Instance"
#define RunKey         "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; Never change AppId: it is how Windows recognises an existing installation, and it is how
; IsUpgrade below knows this is not a first install. Distinct from Etch's, Sling's and Drift's.
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}

; Per-user throughout, and deliberately not an option that can be clicked away: elevating
; Setup would resolve {localappdata} and HKCU against the administrator's account, so the
; start-at-sign-in value would land in another user's registry (Sling's installer explains the
; same trap at length).
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Windows 10 1809 or later: the oldest Windows the box was written for (the acrylic backdrop
; falls back to a solid surface before Windows 11 22H2). Without this Setup would install on
; anything and the failure would be the app's first start.
MinVersion=10.0.17763

; x64compatible matches Arm64 Windows too, so this pair allows both and [Files] picks the
; payload with IsArm64. 64-bit mode keeps registry writes out of Wow6432Node.
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64

OutputDir=..\dist
OutputBaseFilename=Hail-Setup
SetupIconFile=..\assets\hail.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LicenseFile=..\LICENSE

CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Offered on a first install only, and ticked: a launcher that is not running when you press
; its shortcut is no launcher. On an upgrade the choice is the one made since, in Hail's own
; settings (or Task Manager), and the installer leaves it alone.
Name: "startup"; \
  Description: "Start {#AppName} when I sign in, so its shortcut always works"; \
  Check: not IsUpgrade

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; \
  Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; \
  Check: not IsArm64
Source: "..\publish\win-arm64\*"; DestDir: "{app}"; \
  Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; \
  Check: IsArm64
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Registry]
; The same value, and the same quoting, that Hail's own Start with Windows switch writes
; (src\Hail.Windows\Startup\StartupRegistration.cs): the two must agree, or the settings
; window would report a copy "elsewhere". No uninstall flag here: CurUninstallStepChanged
; removes it, and only while it still names this installation.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExeName}"""; Tasks: startup

[Run]
; Ticked, so an update started from inside Hail (which quits so it can be replaced) ends with
; Hail running again. skipifsilent: a silent install is somebody scripting it.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[Code]

function IsUpgrade: Boolean;
var
  Previous: String;
begin
  Result := RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1',
    'UninstallString', Previous);
end;

{
  Asks a running Hail to quit through this installation's exe and waits, up to fifteen
  seconds, for it to let go of its mutex. Hail gives its plugins three seconds to stop as it
  quits, so the wait is generous. Returns whether no Hail is running; if one still is, the
  Restart Manager gets its turn.
}
function QuitRunningHail(Executable: String): Boolean;
var
  ResultCode, Waited: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#AppMutexName}') then
    Exit;

  { Without this installation's exe, the Hail running is a copy from somewhere else (a build
    somebody runs by hand): not this installation's to stop, and holding none of its files. }
  if not FileExists(Executable) then
    Exit;

  Exec(Executable, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Waited := 0;
  while CheckForMutexes('{#AppMutexName}') and (Waited < 15000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;

  Result := not CheckForMutexes('{#AppMutexName}');

  { The mutex goes as Hail's last act, a moment before the process and its open files do. }
  if Result then
    Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningHail(ExpandConstant('{app}\{#AppExeName}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  Result := QuitRunningHail(ExpandConstant('{app}\{#AppExeName}'));
  if not Result then
    MsgBox('Hail is still running. Quit it from its icon in the notification area, then uninstall again.',
      mbError, MB_OK);
end;

{
  Removes the start-at-sign-in value only while it names this installation. If it names
  another copy of Hail (a build somebody runs from elsewhere), that was their choice.
}
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  if RegQueryStringValue(HKCU, '{#RunKey}', '{#AppName}', Command)
     and (CompareText(Trim(Command), '"' + ExpandConstant('{app}\{#AppExeName}') + '"') = 0) then
  begin
    RegDeleteValue(HKCU, '{#RunKey}', '{#AppName}');
    { And Task Manager's on/off flag for it, which would otherwise outlive the entry. }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', '{#AppName}');
  end;
end;
