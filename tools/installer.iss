; Inno Setup 6 script for Windows Activator.
;
;   iscc tools\installer.iss
;
; Expects dist\WinAct <version>.exe to exist (run dotnet publish first). The engine is
; embedded in the EXE. The output installer lands in dist\. Inno Setup is not bundled with
; this repository; install it from https://jrsoftware.org/isdl.php.

#define MyAppName "WinAct"
#define MyAppVersion "1.0.7"
#define MyAppExeName "WinAct.exe"

[Setup]
AppId={{6F1D9B2A-4C7E-4A3B-9D21-8B5E0C7A1F34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=local
DefaultDirName={autopf}\WinAct
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=WinAct-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\icon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "..\dist\WinAct {#MyAppVersion}.exe"; DestName: "{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Older installs kept the engine and the notices file beside the EXE; neither ships any more.
Type: files; Name: "{app}\THIRD-PARTY-NOTICES.txt"
Type: files; Name: "{app}\MAS_AIO.cmd"
Type: files; Name: "{app}\MAS_AIO.cmd.bak"
Type: files; Name: "{app}\MAS_AIO.cmd.new"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\MAS_AIO.cmd"
Type: files; Name: "{app}\MAS_AIO.cmd.bak"
Type: files; Name: "{app}\MAS_AIO.cmd.new"
Type: files; Name: "{app}\{#MyAppExeName}.old"
Type: files; Name: "{app}\{#MyAppExeName}.new"
