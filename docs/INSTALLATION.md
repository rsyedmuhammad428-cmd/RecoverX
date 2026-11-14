# Installing RecoverX

## Requirements
- Windows 7 SP1, Windows 8.1, Windows 10 or Windows 11, 32-bit or 64-bit. (Built and tested on Windows 10; Windows 7 SP1 and 11 not yet tested.)
- Microsoft .NET Framework 4.8. Included in Windows 10 (1903+) and Windows 11. On Windows 7 SP1 install it first from
  https://dotnet.microsoft.com/download/dotnet-framework/net48 (it may need recent Windows 7 updates). Setup checks for it and stops with
  a link if it is missing.
- No Visual C++ Redistributable is needed: the native engine imports only `KERNEL32.dll` and `msvcrt.dll`.
- About 10 MB of disk space.

## Install
1. Run `RecoverX-Setup-1.0.1.exe`.
2. Choose **Install for all users** (Program Files; needs administrator rights, recommended) or **Install for me only** (no admin rights).
3. Read the **License Agreement** (MIT License) and choose **I accept the agreement**.
4. Optionally tick **Create a desktop shortcut**.

Silent install (IT deployment): `RecoverX-Setup-1.0.1.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` (add `/CURRENTUSER` for a per-user
install, `/TASKS=desktopicon` for the shortcut, `/LOG=path` for a log).

## What gets installed
| Location | Content |
|---|---|
| `Program Files\RecoverX\` (or `%LOCALAPPDATA%\Programs\RecoverX\`) | `RecoverX.Desktop.exe`, `RecoverX.Core.dll`, `RecoverX.Database.dll`, dependency DLLs, `RecoverX.ico` |
| `...\x64\`, `...\x86\` | `recoverx_engine.dll` (native recovery engine) and `SQLite.Interop.dll`; the app loads the one matching its process |
| Start Menu | RecoverX shortcut and an uninstall shortcut |
| `%LOCALAPPDATA%\RecoverX\` (created on first run, per user) | `recoverx.db` (settings, scan and recovery history - metadata only), `logs\` |

RecoverX never installs drivers or services and does not connect to the internet.

## First run
RecoverX starts as a normal app. Reading a drive needs administrator rights: use **Restart as administrator** on the Drive Scan page (Windows
shows a UAC prompt). Recover files to a **different drive** than the one you are recovering from; RecoverX enforces this.

## Uninstall
Settings > Apps > RecoverX > Uninstall, or Start Menu > RecoverX > Uninstall. Silent: `unins000.exe /VERYSILENT` in the install folder.
The program folder is removed. Each user's `%LOCALAPPDATA%\RecoverX` (settings, history, logs) is kept; delete it by hand if you want.
Recovered files are never touched.

## Building the installer
Install Inno Setup 6.3+ (https://jrsoftware.org/isdl.php), then run `scripts\build-installer.ps1`. It builds the engine and the app
(Release, into `installer\stage\`), checks that the exe, both engine DLLs and both SQLite interop DLLs are present, and compiles
`installer\RecoverX.iss` into `installer\output\RecoverX-Setup-<version>.exe` (version from `Directory.Build.props`).
