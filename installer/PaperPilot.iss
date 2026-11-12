; Paper Pilot installer (Inno Setup 6). Built by CI:
;   ISCC.exe /DAppVersion=0.3.20 installer\PaperPilot.iss
; Per-user install (no admin): %LOCALAPPDATA%\Programs\Paper Pilot, listed in Installed apps.
; The app updates itself by downloading the next release's setup and running it silently.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "Paper Pilot"
#define AppExe "PaperPilot.exe"
#define AppMutex "Local\PaperPilot.SingleInstance"

[Setup]
; Never change AppId: it's how Windows knows a new version replaces the old one.
AppId={{18C2ACB4-41E7-4F0D-B146-E9E07794725B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL=https://github.com/tobijeh/PaperPilot
AppSupportURL=https://github.com/tobijeh/PaperPilot
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish
OutputBaseFilename=PaperPilot-Setup
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSizePercent=100
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Paper Pilot is closed by our own code below (it holds global hotkeys and input hooks).
CloseApplications=no
RestartApplications=no

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nPaper Pilot scrolls your notes while you preach, so you can look up. It lives in the tray by the clock.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; "Start with Windows" is switched on from Settings; make sure uninstalling removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#AppName}"; ValueType: none; Flags: uninsdeletevalue dontcreatekey

[Run]
; Normal install: offer to start it.
Filename: "{app}\{#AppExe}"; Description: "Start Paper Pilot"; Flags: nowait postinstall skipifsilent
; Silent update from inside the app ("Restart to update"): start it again, quietly in the tray.
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait; Check: LaunchAfterSilentUpdate

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopPaperPilot"

[Code]
function LaunchAfterSilentUpdate: Boolean;
begin
  // The app passes /nolaunch=1 when it updates on quit, so it doesn't reopen.
  Result := WizardSilent and (ExpandConstant('{param:nolaunch|0}') <> '1');
end;

procedure StopRunningCopy;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

function InitializeSetup: Boolean;
var
  Tries: Integer;
begin
  Result := True;
  if WizardSilent then
  begin
    // Updating from the app: it is quitting right now. Give it a moment, then make sure.
    Tries := 0;
    while CheckForMutexes('{#AppMutex}') and (Tries < 40) do
    begin
      Sleep(250);
      Tries := Tries + 1;
    end;
    if CheckForMutexes('{#AppMutex}') then StopRunningCopy;
  end
  else if CheckForMutexes('{#AppMutex}') then
  begin
    if MsgBox('Paper Pilot is running. Close it and continue?', mbConfirmation, MB_OKCANCEL) = IDOK then
      StopRunningCopy
    else
      Result := False;
  end;
end;
