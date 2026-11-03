; ZyZoom Internet Downloader - Inno Setup 5.6.1 (Windows 7 SP1 or newer)
; Build with: make-setup.cmd (uses ISCC.exe 5.6.1)

#define AppName "ZyZoom Internet Downloader"
#define AppVersion "1.0"
#define AppPublisher "ZyZoom"
#define AppExe "ZID.exe"

[Setup]
AppId={{DDC0DC97-ABA3-4576-8181-115323A86A94}
AppName={#AppName}
AppVerName={#AppName} {#AppVersion}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={pf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes
OutputDir=bin
OutputBaseFilename=setup_{#AppVersion}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=6.1sp1
VersionInfoVersion=1.0.0.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

; Lets the browser extension cold-start the app before its first manual run.
[Registry]
Root: HKCU; Subkey: "Software\Classes\zid"; ValueType: string; ValueName: ""; ValueData: "URL:ZyZoom Internet Downloader"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\zid"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\zid\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKCU; Subkey: "Software\Classes\zid\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Files]
Source: "bin\Release\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\ui.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\app.ico"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "bin\Release\download.ico"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "README.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "bin\Release\lang\*"; DestDir: "{app}\lang"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "bin\Release\zid-crx\*"; DestDir: "{app}\zid-crx"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "bin\Release\zid-crx109\*"; DestDir: "{app}\zid-crx109"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "bin\Release\zid-xpi\*"; DestDir: "{app}\zid-xpi"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "bin\Release\zid-firefox-*.xpi"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\Readme"; Filename: "{app}\README.html"; WorkingDir: "{app}"; IconFilename: "{app}\app.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallRun]
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Classes\zid"" /f"; Flags: runhidden
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Classes\zyzoom"" /f"; Flags: runhidden

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  NeedsRestart := False;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /t /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /t /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
