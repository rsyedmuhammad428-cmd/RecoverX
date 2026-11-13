<#
.SYNOPSIS  Full build: native engine (x64 + x86) with its tests, the .NET solution, and the managed tests in 64-bit and 32-bit processes.
.PARAMETER ToolsDir    Portable tools folder (see scripts\setup-portable-tools.ps1). The dotnet SDK there is used when dotnet is not on PATH.
.PARAMETER Toolchain   Passed to build-native.ps1 ("mingw" or "msvc").
.PARAMETER SkipNative  Use the engine already staged in native-bin\.
Verified on Windows 10 22H2 with the portable tools (.NET SDK 8.0.428, CMake 4.5.0, GCC 16.2 msvcrt).
#>
param([string]$ToolsDir = "", [string]$Toolchain = "", [switch]$SkipNative)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($ToolsDir -eq "") { $ToolsDir = Join-Path (Split-Path $root -Parent) "tools" }
$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (& dotnet --list-sdks 2>$null)) {
    $dotnet = Join-Path $ToolsDir "dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { throw "No .NET SDK found. Install one or run scripts\setup-portable-tools.ps1." }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
Push-Location $root
try {
    if (-not $SkipNative) { & (Join-Path $PSScriptRoot "build-native.ps1") -ToolsDir $ToolsDir -Toolchain $Toolchain }
    & python (Join-Path $PSScriptRoot "check-localization.py")
    if ($LASTEXITCODE -ne 0) { throw "localization check failed" }
    & $dotnet build RecoverX.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    foreach ($arch in "x64", "x86") {
        Write-Host "=== managed tests ($arch process) ===" -ForegroundColor Cyan
        & $dotnet test tests\CoreTests\RecoverX.CoreTests.csproj -c Release --no-build -- RunConfiguration.TargetPlatform=$arch
        if ($LASTEXITCODE -ne 0) { throw "managed tests failed ($arch)" }
    }
    Write-Host "Build and tests succeeded. Run: src\RecoverX.Desktop\bin\Release\net48\RecoverX.Desktop.exe" -ForegroundColor Green
} finally { Pop-Location }
