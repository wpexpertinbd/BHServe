; BHServe (Windows) installer — Inno Setup. Produces a branded BHServe-Setup.exe
; that installs the unpackaged WinUI app to Program Files. Build with: iscc bhserve.iss
; (after `dotnet publish` puts the app under ..\publish\). Unsigned for now — users
; click "More info -> Run anyway" on SmartScreen (the Windows analog of macOS "Open Anyway").

#define MyAppName "BHServe"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "BiswasHost"
#define MyAppExe "BHServe.App.exe"

[Setup]
AppId={{B1535E2E-0000-4BHS-0000-000000000001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/wpexpertinbd/BHServe
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=BHServe-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
WizardStyle=modern
; SetupIconFile=..\src\BHServe.App\Assets\AppIcon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
; dotnet publish output (self-contained) goes to ..\publish\ — copy it all.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch BHServe"; Flags: nowait postinstall skipifsilent
