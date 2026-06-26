; BHServe (Windows) installer — Inno Setup. Produces a branded BHServe-Setup.exe
; that installs the unpackaged WinUI app to Program Files. Build with: iscc bhserve.iss
; (after `dotnet publish` puts the app under ..\publish\). Unsigned for now — users
; click "More info -> Run anyway" on SmartScreen (the Windows analog of macOS "Open Anyway").

#define MyAppName "BHServe"
#define MyAppVersion "1.0.11"
#define MyAppPublisher "BiswasHost"
#define MyAppExe "BHServe.App.exe"
#define MyAppURL "https://www.biswashost.com"

[Setup]
AppId={{8F3A1C2E-9B4D-4E6F-A1B2-C3D4E5F60718}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://github.com/wpexpertinbd/BHServe
AppUpdatesURL=https://github.com/wpexpertinbd/BHServe/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Inno 6 hides the Welcome page by default — show it so the branded intro + website link appear.
DisableWelcomePage=no
OutputDir=dist
OutputBaseFilename=BHServe-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
WizardStyle=modern
SetupIconFile=..\src\BHServe.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
; Updating over a running BHServe: force-close any leftover instance (incl. the tray app, which
; otherwise hides-to-tray instead of exiting and keeps BHServe.App.exe / Core.dll locked) so the
; files can be replaced without "Setup was unable to close all applications". We relaunch via [Run],
; so don't let the Restart Manager also restart it (that would double-launch).
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Branded intro (mirrors the macOS installer's welcome screen).
WelcomeLabel1=Welcome to BHServe
WelcomeLabel2=Your own free local web server for Windows - a clean alternative to XAMPP, Laragon, WAMP and MAMP.%n%nThis installs BHServe into your Program Files. It includes:%n%n      -   Multiple PHP versions (7.4, 8.1-8.6), per site%n      -   nginx & Apache, MariaDB / MySQL / PostgreSQL%n      -   Redis & Memcached, Node.js (multiple versions)%n      -   phpMyAdmin, Adminer, Mailpit, trusted HTTPS + *.test domains%n      -   One-click WordPress / PHP sites with auto database%n      -   Share any site publicly with one click (Cloudflare tunnel)%n%nCompletely free & open-source - built with love by BiswasHost.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "addtopath"; Description: "Add the bhserve CLI to PATH"; GroupDescription: "Command line:"

[Files]
; dotnet publish output (self-contained) goes to ..\publish\ — copy it all.
; This includes BHServe.App.exe (GUI), bhserve.exe (CLI), bhserve-elevate.exe (UAC helper).
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef Bundle
; Bundled server binaries (nginx/php/mysql/redis/...) so the install needs NO runtime
; downloads — which is what stops antivirus flagging bhserve.exe as a downloader.
Source: "..\payload\bin\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Append the install dir to the system PATH so `bhserve` works from any terminal.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
    Tasks: addtopath; Check: NeedsAddPath('{app}')

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch BHServe"; Flags: nowait postinstall skipifsilent

[Code]
procedure OpenWebsite(Sender: TObject);
var ErrorCode: Integer;
begin
  ShellExec('open', '{#MyAppURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

// Add a clickable "www.biswashost.com" link near the bottom of the welcome page.
procedure InitializeWizard;
var Link: TNewStaticText;
begin
  // Free up a strip at the bottom of the welcome text for the link.
  WizardForm.WelcomeLabel2.Height := WizardForm.WelcomePage.Height - WizardForm.WelcomeLabel2.Top - ScaleY(34);
  Link := TNewStaticText.Create(WizardForm);
  Link.Parent := WizardForm.WelcomePage;
  Link.Caption := 'www.biswashost.com';
  Link.Cursor := crHand;
  Link.Font.Style := [fsUnderline];
  Link.Font.Color := clBlue;
  Link.OnClick := @OpenWebsite;
  Link.Left := WizardForm.WelcomeLabel2.Left;
  Link.Top := WizardForm.WelcomePage.Height - ScaleY(26);
end;

function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKLM,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
    'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  // Only add if our dir isn't already on PATH (case-insensitive, delimited match).
  Result := Pos(';' + Uppercase(ExpandConstant(Param)) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;
