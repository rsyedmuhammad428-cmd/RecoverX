; RecoverX installer (Inno Setup 6.3 or later). Build with scripts\build-installer.ps1, which stages the app in installer\stage\
; and passes the version from Directory.Build.props:  ISCC.exe /DAppVersion=1.0.0 installer\RecoverX.iss
; Output: installer\output\RecoverX-Setup-<version>.exe
;
; What it installs: the WPF app, the native recovery engine (x64\ and x86\recoverx_engine.dll), SQLite (System.Data.SQLite with
; its x64\/x86\ interop DLLs), dependency DLLs, the icon, a Start Menu shortcut, an optional desktop shortcut, and an uninstaller.
; Requires Windows 7 SP1 or later and .NET Framework 4.8 (checked before installing; setup offers the download page if missing).
; The app runs as a normal user and asks for administrator rights only when it scans a drive.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "RecoverX"
#define AppPublisher "RazaZaidi.AI"
#define AppExe "RecoverX.Desktop.exe"
#define Stage "stage"

[Setup]
AppId={{7C4E2B9A-5D31-4F8E-9A6B-2E1D0C3F8B47}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=Your Data. Another Chance. - Powered by RazaZaidi.AI
AppCopyright=Powered by RazaZaidi.AI
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Program Files needs administrator rights (and protects the program's DLLs from being replaced by non-admin users).
; A per-user install (no admin) can be chosen in the dialog or with /CURRENTUSER.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x86compatible x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
OutputDir=output
OutputBaseFilename=RecoverX-Setup-{#AppVersion}
SetupIconFile=assets\RecoverX.ico
; License page: the user must accept the MIT License before installing.
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Footer text shown at the bottom-left of every setup page.
BeveledLabel=Powered by RazaZaidi.AI

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything the build produced, including x64\ and x86\ native folders. Debug symbols are not shipped.
Source: "{#Stage}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "assets\RecoverX.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\RecoverX.ico"; Comment: "Recover deleted files"
Name: "{group}\{#AppName} License"; Filename: "{app}\LICENSE.txt"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\RecoverX.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Program folder only. Settings, scan history and logs in %LOCALAPPDATA%\RecoverX belong to each user and are kept;
; recovered files are never touched.
Type: dirifempty; Name: "{app}"

[Code]
// .NET Framework 4.8 = release 528040 or higher (528049 on Windows 10 1903+, 528372 on Windows 7 SP1 with the 4.8 installer).
function IsDotNet48Installed(): Boolean;
var release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', release) and (release >= 528040);
end;

function InitializeSetup(): Boolean;
var code: Integer;
begin
  Result := True;
  if not IsDotNet48Installed() then
  begin
    if SuppressibleMsgBox('RecoverX needs Microsoft .NET Framework 4.8, which is not installed on this computer.' + #13#10#13#10 +
        'Open the Microsoft download page now? Install .NET Framework 4.8, restart if asked, then run this setup again.',
        mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet-framework/net48', '', '', SW_SHOWNORMAL, ewNoWait, code);
    Result := False;
  end;
end;
