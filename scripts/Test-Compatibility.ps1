<#
.SYNOPSIS
  Collects evidence of how RecoverX behaves on THIS Windows machine and writes a report file to send back.
  Run it on each target OS (Windows 7 SP1, Windows 10, Windows 11) - the result only counts for the machine it ran on.
  Read-only: it changes nothing except creating the report file. Works with Windows PowerShell 2.0+ (Windows 7).
.PARAMETER NativeBin  Folder containing x64\ and x86\ engine builds (default: ..\native-bin, or the app folder).
.PARAMETER AppExe     Path to RecoverX.Desktop.exe if you want its presence checked.
.PARAMETER OutDir     Where to write compat-report-*.txt (default: current folder).
STATUS: written on Linux, not yet executed on any Windows version.
#>
param([string]$NativeBin = "", [string]$AppExe = "", [string]$OutDir = ".")

$stamp  = Get-Date -Format "yyyyMMdd-HHmmss"
$report = Join-Path $OutDir ("compat-report-" + $env:COMPUTERNAME + "-" + $stamp + ".txt")
$script:fail = 0; $script:warn = 0
function Out-Both($text) { Write-Host $text; $text | Out-File -FilePath $report -Append -Encoding UTF8 }
function Check($name, $pass, $detail) {
    if ($pass) { Out-Both ("[PASS] " + $name + "  " + $detail) }
    else       { Out-Both ("[FAIL] " + $name + "  " + $detail); $script:fail++ }
}
function Note($name, $detail) { Out-Both ("[WARN] " + $name + "  " + $detail); $script:warn++ }

if ($NativeBin -eq "") { $NativeBin = Join-Path $PSScriptRoot "..\native-bin" }

Out-Both "RecoverX compatibility report"
Out-Both ("Generated: " + (Get-Date))
Out-Both "----- Environment -----"
$os = Get-WmiObject Win32_OperatingSystem
Out-Both ("OS:            " + $os.Caption)
Out-Both ("Version/Build: " + $os.Version + " (SP " + $os.ServicePackMajorVersion + ")")
Out-Both ("Architecture:  " + $os.OSArchitecture)
Out-Both ("PowerShell:    " + $PSVersionTable.PSVersion)
$rel = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -ErrorAction SilentlyContinue).Release
Out-Both (".NET release:  " + $rel + "  (528040 or higher = 4.8)")
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Out-Both ("Elevated:      " + $isAdmin)

Out-Both "----- Prerequisites -----"
$ver = [version]$os.Version
if ($ver.Major -eq 6 -and $ver.Minor -eq 1) { Check "Windows 7 has Service Pack 1" ($os.ServicePackMajorVersion -ge 1) ("SP=" + $os.ServicePackMajorVersion) }
Check "Windows 7 SP1 or newer" ($ver -ge [version]"6.1.7601") ($os.Version)
Check ".NET Framework 4.8 installed" ($rel -ge 528040) ("release=" + $rel)

$arch = "x86"; if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64" -or $env:PROCESSOR_ARCHITEW6432 -eq "AMD64") { $arch = "x64" }
$dll  = Join-Path $NativeBin ($arch + "\recoverx_engine.dll")
Check ("Native engine present for " + $arch) (Test-Path $dll) $dll
if ($AppExe -ne "") { Check "RecoverX.Desktop.exe present" (Test-Path $AppExe) $AppExe }

Out-Both "----- Native engine self-test (read-only) -----"
$self = Join-Path $NativeBin ($arch + "\rx_selftest.exe")
if (Test-Path $self) {
    $output = & $self 2>&1
    $code = $LASTEXITCODE
    $output | ForEach-Object { Out-Both ("  " + $_) }
    Check "rx_selftest exit code is 0" ($code -eq 0) ("exit=" + $code)
    if (-not $isAdmin) { Note "Not elevated" "Raw read probes report 'access denied'. Re-run from an elevated PowerShell for the full test." }
} else { Note "rx_selftest.exe not found" $self }

Out-Both "----- Result of automatic checks -----"
Out-Both ("Failures: " + $script:fail + "   Warnings: " + $script:warn)

Out-Both "----- Manual checks (fill in PASS / FAIL / N-A and add notes) -----"
@(
 "[ ] 1. RecoverX.Desktop.exe starts and shows the Dashboard without an error dialog",
 "[ ] 2. Drive cards show the correct drive letters, sizes and file systems",
 "[ ] 3. A known SSD shows 'SSD / flash'; a known spinning HDD shows 'HDD'; note any that show Unknown",
 "[ ] 4. A USB flash drive / USB disk shows Connection 'USB' and 'External (USB)'",
 "[ ] 5. An internal SATA/NVMe disk shows 'Internal'",
 "[ ] 6. About page lists OS, .NET, process bitness and the native engine version (not 'not loaded')",
 "[ ] 7. Light/Dark theme both readable (radio buttons and check boxes legible in Dark)",
 "[ ] 8. Settings: change each setting, restart the app, values are still there",
 "[ ] 9. Scan History: 'Clear history' asks for confirmation (list is empty on a fresh install)",
 "[ ] 10. Not elevated: 'Verify read access' reports access denied with a clear message",
 "[ ] 11. 'Restart as administrator' shows the UAC prompt; after approving, Verify read access shows the correct file system",
 "[ ] 12. Window is usable at 125% and 150% display scaling",
 "[ ] 13. Drive Scan > Quick Scan on an NTFS drive (elevated): confirmation dialog, progress, then a results list",
 "[ ] 14. A file you deleted a minute ago appears with condition 'Looks intact' (or 'Probably recoverable')",
 "[ ] 15. Recover it to a DIFFERENT drive; the file opens correctly; its modified date matches the original",
 "[ ] 16. Choosing a recovery folder on the scanned drive is refused with a clear message",
 "[ ] 17. Cancel during a scan and during a recovery: no crash, no half-written files left",
 "[ ] 18. Recovered Files page lists what you recovered; 'Show in folder' opens it",
 "[ ] 19. Run scripts\\Test-NtfsVhd.ps1 elevated: RESULT: PASS (attach its report)",
 "Tester: ______________   Hardware: ______________   Notes: ______________________________"
) | ForEach-Object { Out-Both $_ }

Write-Host ""
Write-Host ("Report saved to: " + $report) -ForegroundColor Cyan
