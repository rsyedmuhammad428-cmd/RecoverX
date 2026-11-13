# RecoverX - environment check (run in PowerShell on your Windows dev machine). Read-only.
$ok = $true
function Report($name, $found, $detail) {
    if ($found) { Write-Host "[OK]      $name  $detail" -ForegroundColor Green }
    else { Write-Host "[MISSING] $name  $detail" -ForegroundColor Yellow; $script:ok = $false }
}
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = $null
if (Test-Path $vswhere) { $vs = & $vswhere -version "[17.0,18.0)" -products * -property installationPath | Select-Object -First 1 }
Report "Visual Studio 2022" ([bool]$vs) $vs
$rel = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -ErrorAction SilentlyContinue).Release
Report ".NET Framework 4.8 runtime" ($rel -ge 528040) "release=$rel"
Report ".NET Framework 4.8 Developer Pack" (Test-Path "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8") ""
Report "dotnet CLI" ([bool](Get-Command dotnet -ErrorAction SilentlyContinue)) ""
$cl = $null
if ($vs) { $cl = Get-ChildItem "$vs\VC\Tools\MSVC" -ErrorAction SilentlyContinue | Select-Object -First 1 }
Report "MSVC C++ toolset (needed for the native engine)" ([bool]$cl) ""
Report "Windows 10/11 SDK" (Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Include") ""
Report "CMake 3.15+" ([bool](Get-Command cmake -ErrorAction SilentlyContinue)) ""
$tools = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "tools"
$portable = (Test-Path (Join-Path $tools "mingw64/bin/g++.exe")) -and (Test-Path (Join-Path $tools "cmake/bin/cmake.exe")) -and (Test-Path (Join-Path $tools "dotnet/dotnet.exe"))
Write-Host ""
if ($portable) { Write-Host "[OK]      Portable toolchain in $tools (GCC + CMake + .NET SDK): scripts/build-all.ps1 works without Visual Studio." -ForegroundColor Green }
else { Write-Host "[INFO]    No portable toolchain in $tools. Instead of Visual Studio you can run scripts/setup-portable-tools.ps1 (no admin rights needed)." }
Report "Inno Setup 6 (needed from Phase 7)" (Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") ""
if ($ok) { Write-Host "`nAll prerequisites found." } else { Write-Host "`nInstall the missing items above, then run scripts\build-all.ps1." }
