<#
.SYNOPSIS  Builds installer\output\RecoverX-Setup-<version>.exe.
           1. builds the native engine (unless -SkipNative) and stages it in native-bin\
           2. builds the app in Release into installer\stage\ (a separate folder, so a running copy of RecoverX does not block it)
           3. checks that the stage contains the exe, both engine DLLs and both SQLite interop DLLs
           4. compiles installer\RecoverX.iss with Inno Setup's ISCC.exe
.PARAMETER Iscc       Path to ISCC.exe. Default: ..\tools\innosetup\ISCC.exe, then Program Files (x86)\Inno Setup 6\ISCC.exe.
.PARAMETER ToolsDir   Portable tools folder (see setup-portable-tools.ps1).
Inno Setup 6.3+ is required (free: https://jrsoftware.org/isdl.php). It is not downloaded automatically.
#>
param([string]$Iscc = "", [string]$ToolsDir = "", [switch]$SkipNative)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($ToolsDir -eq "") { $ToolsDir = Join-Path (Split-Path $root -Parent) "tools" }
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
Write-Host "RecoverX $version" -ForegroundColor Cyan

if ($Iscc -eq "") {
    foreach ($c in @((Join-Path $ToolsDir "innosetup\ISCC.exe"), "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $c) { $Iscc = $c; break }
    }
}
if ($Iscc -eq "" -or -not (Test-Path $Iscc)) { throw "Inno Setup (ISCC.exe) not found. Install Inno Setup 6.3+ from https://jrsoftware.org/isdl.php or pass -Iscc." }

if (-not $SkipNative) { & (Join-Path $PSScriptRoot "build-native.ps1") -ToolsDir $ToolsDir }

$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (& dotnet --list-sdks 2>$null)) { $dotnet = Join-Path $ToolsDir "dotnet\dotnet.exe" }
$stage = Join-Path $root "installer\stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
& $dotnet build (Join-Path $root "src\RecoverX.Desktop\RecoverX.Desktop.csproj") -c Release "-p:OutDir=$stage\"
if ($LASTEXITCODE -ne 0) { throw "Release build failed" }

$required = "RecoverX.Desktop.exe", "RecoverX.Core.dll", "RecoverX.Database.dll", "System.Data.SQLite.dll",
            "x64\recoverx_engine.dll", "x86\recoverx_engine.dll", "x64\SQLite.Interop.dll", "x86\SQLite.Interop.dll"
foreach ($f in $required) { if (-not (Test-Path (Join-Path $stage $f))) { throw "Staged build is missing $f" } }

& $Iscc "/DAppVersion=$version" (Join-Path $root "installer\RecoverX.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed" }
$out = Join-Path $root "installer\output\RecoverX-Setup-$version.exe"
Write-Host ("Built " + $out + " (" + [Math]::Round((Get-Item $out).Length / 1MB, 1) + " MB)") -ForegroundColor Green
