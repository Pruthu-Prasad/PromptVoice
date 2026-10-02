; PromptVoice installer (Inno Setup 6)
;
; Per-user install into %LOCALAPPDATA%\Programs\PromptVoice:
;   - no UAC prompt, works on a locked-down machine
;   - the app writes nothing here at runtime (see AppPaths.cs); user data lives
;     in %LOCALAPPDATA%\PromptVoice and survives uninstall unless asked otherwise
;
; Build with installer\build-installer.ps1 - it publishes first and passes
; AppVersion in. Do not compile this file directly against a stale publish.

#define AppName        "PromptVoice"
#define AppPublisher   "PromptVoice"
#define AppExeName     "PromptVoice.exe"
#ifndef AppVersion
  #define AppVersion   "1.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir   "..\PromptVoice\publish"
#endif

[Setup]
AppId={{7C2A5E11-4F3B-4E8D-9B27-2F6C1D0A83E5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per-user: no administrator rights required at any point.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; Single self-contained .exe output.
OutputDir=..\dist
OutputBaseFilename=PromptVoice-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
InternalCompressLevel=max

WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; The app is a tray app with a global hook; it must not be running during an
; upgrade. AppMutex matches the single-instance mutex in Program.cs.
CloseApplications=no
RestartApplications=no

; Headroom beyond the extracted payload; refuse early rather than failing
; halfway through extraction.
ExtraDiskSpaceRequired=52428800

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startupicon"; Description: "Start PromptVoice when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; Everything from the publish folder, including runtime\ with whisper-cli.exe,
; its DLLs, and the bundled base.en model.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Only written when the user ticks the task; the app owns this value afterwards
; and its Settings toggle adds or removes it the same way.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "PromptVoice"; \
    ValueData: """{app}\{#AppExeName}"" --autostart"; \
    Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the tray app before files are removed, so the folder is not locked.
Filename: "taskkill.exe"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "StopPromptVoice"

[Code]
var
  RemoveUserData: Boolean;

{ A tray app with no main window cannot be closed by Restart Manager, so a
  silent upgrade would otherwise hang forever waiting on it. Stop it outright
  before files are replaced; the post-install Run step starts the new one. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PromptVoice.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;

{ The startup entry is per-user and written by the app too, so always clear it
  on uninstall regardless of which one created it. }
procedure RemoveStartupEntry;
begin
  RegDeleteValue(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run', 'PromptVoice');
end;

function InitializeUninstall(): Boolean;
begin
  { A silent uninstall must never block on a dialog, so keep user data in that
    case - deleting someone's history without asking is the worse default. }
  if UninstallSilent then
    RemoveUserData := False
  else
    RemoveUserData := MsgBox(
      'Also delete your PromptVoice settings, dictation history, and any models you downloaded?'#13#10#13#10 +
      'Choose No to keep them for a future reinstall.',
      mbConfirmation, MB_YESNO) = IDYES;

  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveStartupEntry;

    if RemoveUserData then
    begin
      DataDir := ExpandConstant('{localappdata}\PromptVoice');
      if DirExists(DataDir) then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
