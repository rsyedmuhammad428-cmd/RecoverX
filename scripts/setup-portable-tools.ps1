<#
.SYNOPSIS  Downloads a portable build toolchain into a folder next to the repository (no administrator rights, no system changes):
           .NET SDK 8 (dotnet-install script), CMake 4.5.0, and WinLibs GCC 16.2 for x64 and x86 (msvcrt runtime, which keeps the
           engine DLL free of the Universal CRT so it loads on Windows 7 SP1 without extra updates). About 1 GB on disk.
           Delete the folder to remove everything.
.PARAMETER ToolsDir  Default: ..\..\tools (next to the repository folder).
#>
param([string]$ToolsDir = "")
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($ToolsDir -eq "") { $ToolsDir = Join-Path (Split-Path $root -Parent) "tools" }
$dl = Join-Path $ToolsDir "dl"
New-Item -ItemType Directory -Force -Path $dl | Out-Null

$winlibs = "https://github.com/brechtsanders/winlibs_mingw/releases/download/16.2.0posix-23.1.3-14.0.0-msvcrt-r3"
$items = @(
    @{ Url = "https://github.com/Kitware/CMake/releases/download/v4.5.0/cmake-4.5.0-windows-x86_64.zip"; File = "cmake.zip" },
    @{ Url = "$winlibs/winlibs-x86_64-posix-seh-gcc-16.2.0-mingw-w64msvcrt-14.0.0-r3.zip"; File = "mingw64.zip" },
    @{ Url = "$winlibs/winlibs-i686-posix-dwarf-gcc-16.2.0-mingw-w64msvcrt-14.0.0-r3.zip"; File = "mingw32.zip" },
    @{ Url = "https://dot.net/v1/dotnet-install.ps1"; File = "dotnet-install.ps1" }
)
foreach ($i in $items) {
    $target = Join-Path $dl $i.File
    if (Test-Path $target) { Write-Host "have $($i.File)"; continue }
    Write-Host "downloading $($i.File) ..."
    for ($try = 1; $try -le 5; $try++) {
        try { Invoke-WebRequest -Uri $i.Url -OutFile $target -UseBasicParsing; break }
        catch { if ($try -eq 5) { throw }; Start-Sleep -Seconds (3 * $try) }
    }
}
if (-not (Test-Path (Join-Path $ToolsDir "cmake\bin\cmake.exe"))) {
    Expand-Archive (Join-Path $dl "cmake.zip") $ToolsDir -Force
    Rename-Item (Join-Path $ToolsDir "cmake-4.5.0-windows-x86_64") "cmake"
}
if (-not (Test-Path (Join-Path $ToolsDir "mingw64\bin\g++.exe"))) { Expand-Archive (Join-Path $dl "mingw64.zip") $ToolsDir -Force }
if (-not (Test-Path (Join-Path $ToolsDir "mingw-i686\mingw32\bin\g++.exe"))) { Expand-Archive (Join-Path $dl "mingw32.zip") (Join-Path $ToolsDir "mingw-i686") -Force }
if (-not (Test-Path (Join-Path $ToolsDir "dotnet\dotnet.exe"))) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dl "dotnet-install.ps1") -Channel 8.0 -InstallDir (Join-Path $ToolsDir "dotnet") -NoPath
}
Write-Host "Portable tools ready in $ToolsDir. Next: scripts\build-all.ps1" -ForegroundColor Green
