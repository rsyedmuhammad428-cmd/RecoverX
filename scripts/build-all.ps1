<#
.SYNOPSIS  Full build: native engine (x64 + x86), .NET solution, managed unit tests.
Requires: Visual Studio 2022 (.NET desktop + C++ workloads), CMake, .NET SDK (for the dotnet CLI), .NET Framework 4.8 Developer Pack.
STATUS: not yet run on Windows.
#>
param([switch]$SkipNative)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $root
try {
    if (-not $SkipNative) { & (Join-Path $PSScriptRoot "build-native.ps1") }
    & dotnet restore RecoverX.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
    & dotnet build RecoverX.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    & dotnet test tests\CoreTests\RecoverX.CoreTests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "managed unit tests failed" }
    Write-Host "Build and tests succeeded. Run: src\RecoverX.Desktop\bin\Release\net48\RecoverX.Desktop.exe" -ForegroundColor Green
} finally { Pop-Location }
