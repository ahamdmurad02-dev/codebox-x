; CodeBox X — Windows x64 installer
; Builds a real per-machine installer from the self-contained WPF publish output.

#define AppName "CodeBox X"
#define AppVersion "1.2.1"
#define AppPublisher "CodeBox X"
#define AppExeName "CodeBoxX.exe"
#ifndef PublishDir
  #define PublishDir "..\\publish\\win-x64-selfcontained"
#endif

[Setup]
AppId={{AFA45E3F-9498-4D0B-88D5-2B093F258A3D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\CodeBox X
DefaultGroupName=CodeBox X
DisableProgramGroupPage=yes
OutputDir=..\installer
OutputBaseFilename=CodeBoxX-Setup-win-x64
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=2
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
UninstallDisplayName=CodeBox X
UninstallDisplayIcon={app}\{#AppExeName}
SetupLogging=yes
SetupIconFile=..\CodeBoxX\Assets\CodeBoxX.ico
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CodeBox X"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\CodeBox X\Uninstall CodeBox X"; Filename: "{uninstallexe}"
Name: "{autodesktop}\CodeBox X"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch CodeBox X"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then begin
    MsgBox('CodeBox X is available only for 64-bit Windows 10 or Windows 11.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    Log('Installing self-contained CodeBox X Windows x64 build.');
    Log('Live Preview requires the Microsoft Edge WebView2 Runtime, normally included with current Microsoft Edge and Windows 11.');
  end;
end;
