<#
.SYNOPSIS  Builds the RecoverX native engine for x64 and x86 and stages it in native-bin\ for the WPF app.
.PARAMETER Generator  CMake generator (default: Visual Studio 17 2022).
.PARAMETER Toolset    Optional MSVC toolset override, e.g. v142. Leave empty for the generator default.
.PARAMETER Clean      Delete previous build folders first.
Requires: Visual Studio 2022 "Desktop development with C++", CMake 3.15+.   STATUS: not yet run on Windows.
#>
param([string]$Generator = "Visual Studio 17 2022", [string]$Toolset = "", [switch]$Clean)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$targets = @( @{ Name = "x64"; Arch = "x64" }, @{ Name = "x86"; Arch = "Win32" } )

foreach ($t in $targets) {
    $build = Join-Path $root ("build\native-" + $t.Name)
    if ($Clean -and (Test-Path $build)) { Remove-Item -Recurse -Force $build }

    $cfg = @("-S", $root, "-B", $build, "-G", $Generator, "-A", $t.Arch)
    if ($Toolset -ne "") { $cfg += @("-T", $Toolset) }
    Write-Host "=== Configure $($t.Name) ===" -ForegroundColor Cyan
    & cmake @cfg
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed for $($t.Name)" }

    Write-Host "=== Build $($t.Name) ===" -ForegroundColor Cyan
    & cmake --build $build --config Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $($t.Name)" }

    Write-Host "=== Test $($t.Name) ===" -ForegroundColor Cyan
    Push-Location $build
    try { & ctest -C Release --output-on-failure; if ($LASTEXITCODE -ne 0) { throw "Native unit tests failed for $($t.Name)" } }
    finally { Pop-Location }

    $out = Join-Path $root ("native-bin\" + $t.Name)
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $rel = Join-Path $build "src\RecoverX.Native\Release"
    Copy-Item (Join-Path $rel "recoverx_engine.dll") $out -Force
    Copy-Item (Join-Path $rel "rx_selftest.exe") $out -Force
    Write-Host "Staged $($t.Name) engine in $out" -ForegroundColor Green
}
Write-Host "Done. Next: scripts\build-all.ps1 (or open RecoverX.sln in Visual Studio)." -ForegroundColor Green
