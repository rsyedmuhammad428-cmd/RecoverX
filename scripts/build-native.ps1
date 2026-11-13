<#
.SYNOPSIS  Builds the RecoverX native engine for x64 and x86, runs the native test suites, and stages the DLLs in native-bin\.
.PARAMETER Toolchain  "mingw" (portable GCC, see scripts\setup-portable-tools.ps1) or "msvc" (Visual Studio 2022). Default: mingw
                      when the portable tools exist, otherwise msvc.
.PARAMETER ToolsDir   Folder holding the portable tools (cmake\, mingw64\, mingw-i686\). Default: ..\..\tools next to the repo.
.PARAMETER Toolset    MSVC only: optional toolset override, e.g. v142.
.PARAMETER Clean      Delete previous build folders first.
.PARAMETER SkipTests  Build and stage only.
Verified with the mingw toolchain (GCC 16.2, msvcrt runtime) on Windows 10 22H2: all native suites pass for x64 and x86.
The msvc path has not been run yet.
#>
param([ValidateSet("", "mingw", "msvc")][string]$Toolchain = "", [string]$ToolsDir = "", [string]$Toolset = "",
      [switch]$Clean, [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($ToolsDir -eq "") { $ToolsDir = Join-Path (Split-Path $root -Parent) "tools" }
if ($Toolchain -eq "") { $Toolchain = if (Test-Path (Join-Path $ToolsDir "mingw64\bin\g++.exe")) { "mingw" } else { "msvc" } }
Write-Host "Toolchain: $Toolchain" -ForegroundColor Cyan

$cmake = "cmake"
if (Test-Path (Join-Path $ToolsDir "cmake\bin\cmake.exe")) { $cmake = Join-Path $ToolsDir "cmake\bin\cmake.exe" }

$targets = if ($Toolchain -eq "mingw") {
    @(@{ Name = "x64"; Bin = Join-Path $ToolsDir "mingw64\bin" }, @{ Name = "x86"; Bin = Join-Path $ToolsDir "mingw-i686\mingw32\bin" })
} else {
    @(@{ Name = "x64"; Arch = "x64" }, @{ Name = "x86"; Arch = "Win32" })
}

$savedPath = $env:PATH
try {
    foreach ($t in $targets) {
        $build = Join-Path $root ("build\" + $Toolchain + "-" + $t.Name)
        if ($Clean -and (Test-Path $build)) { Remove-Item -Recurse -Force $build }
        if ($Toolchain -eq "mingw") {
            if (-not (Test-Path (Join-Path $t.Bin "g++.exe"))) { throw "MinGW not found in $($t.Bin). Run scripts\setup-portable-tools.ps1 first." }
            $env:PATH = $t.Bin + ";" + $savedPath
            $cfg = @("-S", $root, "-B", $build, "-G", "MinGW Makefiles", "-DCMAKE_BUILD_TYPE=Release")
        } else {
            $cfg = @("-S", $root, "-B", $build, "-G", "Visual Studio 17 2022", "-A", $t.Arch)
            if ($Toolset -ne "") { $cfg += @("-T", $Toolset) }
        }
        Write-Host "=== Configure $($t.Name) ===" -ForegroundColor Cyan
        & $cmake @cfg
        if ($LASTEXITCODE -ne 0) { throw "CMake configure failed for $($t.Name)" }
        Write-Host "=== Build $($t.Name) ===" -ForegroundColor Cyan
        if ($Toolchain -eq "mingw") {
            # mingw32-make run from cmd/PowerShell fails parallel builds with "invalid output sync mutex" unless output sync is off.
            & $cmake --build $build --config Release -j 8 -- --output-sync=none
        } else {
            & $cmake --build $build --config Release -j 8
        }
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $($t.Name)" }

        $rel = if ($Toolchain -eq "mingw") { Join-Path $build "src\RecoverX.Native" } else { Join-Path $build "src\RecoverX.Native\Release" }
        $testDir = if ($Toolchain -eq "mingw") { Join-Path $build "tests\NativeTests" } else { Join-Path $build "tests\NativeTests\Release" }
        if (-not $SkipTests) {
            foreach ($exe in "native_tests", "ntfs_tests", "carve_tests", "fat_tests") {
                Write-Host "=== $exe ($($t.Name)) ===" -ForegroundColor Cyan
                Push-Location $testDir
                try { & (Join-Path $testDir "$exe.exe"); if ($LASTEXITCODE -ne 0) { throw "$exe failed for $($t.Name)" } }
                finally { Pop-Location }
            }
        }
        $out = Join-Path $root ("native-bin\" + $t.Name)
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        Copy-Item (Join-Path $rel "recoverx_engine.dll") $out -Force
        Copy-Item (Join-Path $rel "rx_selftest.exe") $out -Force
        Write-Host "Staged $($t.Name) engine in $out" -ForegroundColor Green
    }
} finally { $env:PATH = $savedPath }
Write-Host "Done. Next: scripts\build-all.ps1 -SkipNative (or open RecoverX.sln in Visual Studio)." -ForegroundColor Green
