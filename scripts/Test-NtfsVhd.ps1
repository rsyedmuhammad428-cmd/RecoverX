<#
.SYNOPSIS
  End-to-end NTFS recovery test on a DISPOSABLE virtual disk. Creates a small VHD, formats it NTFS, writes files whose
  SHA-256 is known, deletes them, runs the RecoverX engine's Quick Scan against the VHD, and compares the recovered bytes.
  Nothing on your real drives is touched: only the temporary VHD (deleted at the end) is scanned.

  Run from an ELEVATED PowerShell on each Windows version you want to test (Windows 7 SP1 / 10 / 11), after scripts\build-native.ps1.
  Works with Windows PowerShell 2.0+. Uses diskpart (present on all supported versions).

.PARAMETER NativeBin  Folder with x64\ and x86\ engine builds (default ..\native-bin).
.PARAMETER Letter     Drive letter to give the VHD (default: first free letter from Z downwards).
.PARAMETER KeepVhd    Keep the VHD file afterwards for inspection.

STATUS: written on Linux, NOT yet executed on any Windows version. The same scenario was validated against a real NTFS image on Linux.
Exit code 0 = every expectation met.
#>
param([string]$NativeBin = "", [string]$Letter = "", [switch]$KeepVhd)

$ErrorActionPreference = "Stop"
if ($NativeBin -eq "") { $NativeBin = Join-Path $PSScriptRoot "..\native-bin" }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "Run this script from an elevated (Administrator) PowerShell." -ForegroundColor Red; exit 2 }

$arch = "x86"; if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64" -or $env:PROCESSOR_ARCHITEW6432 -eq "AMD64") { $arch = "x64" }
$selftest = Join-Path $NativeBin ($arch + "\rx_selftest.exe")
if (-not (Test-Path $selftest)) { Write-Host "rx_selftest.exe not found at $selftest. Run scripts\build-native.ps1 first." -ForegroundColor Red; exit 2 }

# ---- choose a free drive letter ----
if ($Letter -eq "") {
    $used = @(); foreach ($d in [IO.DriveInfo]::GetDrives()) { $used += $d.Name.Substring(0, 1).ToUpper() }
    foreach ($c in [char[]]"ZYXWVUTSRQPONMLKJ") { if ($used -notcontains [string]$c) { $Letter = [string]$c; break } }
}
$Letter = $Letter.Substring(0, 1).ToUpper()
if ([IO.Directory]::Exists($Letter + ":\")) { Write-Host "Drive $Letter`: is already in use. Pass -Letter with a free letter." -ForegroundColor Red; exit 2 }

$work = Join-Path $env:TEMP ("RecoverX-vhdtest-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$vhd = Join-Path $work "rxtest.vhd"
$expectFile = Join-Path $work "expected.txt"       # kept OUTSIDE the VHD: writing to the VHD after deletion could overwrite deleted data
$report = Join-Path $work "report.txt"
$script:failures = 0

function Log($t) { Write-Host $t; $t | Out-File -FilePath $report -Append -Encoding UTF8 }
function Check($name, $ok, $detail) {
    if ($ok) { Log ("[PASS] " + $name) } else { Log ("[FAIL] " + $name + "  " + $detail); $script:failures++ }
}
function Run-Diskpart($lines) {
    $f = Join-Path $work "dp.txt"
    $lines | Out-File -FilePath $f -Encoding ASCII
    $out = & diskpart.exe /s $f 2>&1
    $out | Out-File -FilePath $report -Append -Encoding UTF8
    return $out
}
function Sha256Hex($bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $h = $sha.ComputeHash($bytes)
    return ([BitConverter]::ToString($h)).Replace("-", "").ToLower()
}
function Get-PlanBytes($x) {
    if ($x -eq $null) { return ,([Array]::CreateInstance([byte], 0)) }
    return ,([byte[]]$x)
}
function Make-Bytes($n, $seed, $head) {
    # unique per 4 KB block (a missing or misplaced cluster changes the hash), deterministic
    $b = New-Object byte[] $n
    $r = New-Object Random $seed
    $r.NextBytes($b)
    for ($i = 0; $i -lt $head.Length -and $i -lt $n; $i++) { $b[$i] = $head[$i] }
    return ,$b
}

Log ("RecoverX NTFS VHD test  " + (Get-Date))
$os = Get-WmiObject Win32_OperatingSystem
Log ("OS: " + $os.Caption + " " + $os.Version + " SP" + $os.ServicePackMajorVersion + " " + $os.OSArchitecture + " | engine: " + $arch)

try {
    # ---- 1. create + format the disposable disk ----
    Log "Creating a 64 MB virtual disk at $vhd as drive $Letter`:"
    Run-Diskpart @("create vdisk file=`"$vhd`" maximum=64 type=expandable", "select vdisk file=`"$vhd`"", "attach vdisk",
                   "create partition primary", "format fs=ntfs quick label=RXTEST", "assign letter=$Letter") | Out-Null
    Start-Sleep -Seconds 3
    Check "VHD is mounted as $Letter`:" ([IO.Directory]::Exists($Letter + ":\")) "diskpart did not mount the volume (see report)"
    if (-not [IO.Directory]::Exists($Letter + ":\")) { throw "VHD not mounted" }
    $root = $Letter + ":\"

    # ---- 2. create files with known content ----
    $plan = @()   # path (relative), bytes, delete?
    $plan += ,@("keep\keep.txt",                  (Make-Bytes 500 1 ([Text.Encoding]::ASCII.GetBytes("keep this file"))),            $false)
    $plan += ,@("docs\small.txt",                 ([Text.Encoding]::ASCII.GetBytes("a short resident text file")),                  $true)
    $plan += ,@("docs\report.pdf",                (Make-Bytes 300000 2 ([Text.Encoding]::ASCII.GetBytes("%PDF-1.4`n"))),             $true)
    $plan += ,@("pics\photo1.jpg",                (Make-Bytes 200000 3 ([byte[]](0xFF,0xD8,0xFF,0xE0,0x00,0x10,0x4A,0x46,0x49,0x46))), $true)
    $plan += ,@("pics\video.mp4",                 (Make-Bytes 3000000 4 ([byte[]](0,0,0,0x18,0x66,0x74,0x79,0x70,0x6D,0x70,0x34,0x32))), $true)
    $plan += ,@("empty.dat",                      $null,                                                                                $true)   # $null = empty file (empty arrays collapse inside array literals)
    $plan += ,@(("docs\r" + [char]0xE9 + "sum" + [char]0xE9 + "_" + [char]0xF1 + ".txt"), ([Text.Encoding]::UTF8.GetBytes("unicode file name test")), $true)
    $plan += ,@("deleted_dir\inner\notes.txt",    ([Text.Encoding]::ASCII.GetBytes("inside a folder that is deleted too")),         $true)
    $plan += ,@("deleted_dir\pic.png",            (Make-Bytes 50000 5 ([byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A))),          $true)
    for ($i = 0; $i -lt 60; $i++) { $plan += ,@(("many\f" + $i.ToString("000") + ".txt"), ([Text.Encoding]::ASCII.GetBytes("small file number " + $i)), ($i % 2 -eq 1)) }

    "# path`tsize`tsha256`tdeleted" | Out-File -FilePath $expectFile -Encoding UTF8
    foreach ($p in $plan) {
        $full = Join-Path $root $p[0]
        $dir = Split-Path $full -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $bytes = Get-PlanBytes $p[1]
        [IO.File]::WriteAllBytes($full, $bytes)
        ("\" + $p[0] + "`t" + $bytes.Length + "`t" + (Sha256Hex $bytes) + "`t" + [int]$p[2]) | Out-File -FilePath $expectFile -Append -Encoding UTF8
    }
    Log ("Created " + $plan.Count + " files")

    # ---- 3. permanent deletion (no Recycle Bin) ----
    foreach ($p in $plan) { if ($p[2]) { $full = Join-Path $root $p[0]; if (Test-Path $full) { Remove-Item -LiteralPath $full -Force } } }
    Remove-Item -LiteralPath (Join-Path $root "deleted_dir") -Recurse -Force
    Start-Sleep -Seconds 2
    Log "Deleted the planned files (no writes to the volume after this point)"

    # ---- 4. scan the VHD with the engine ----
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $scan = & $selftest --scan $Letter --hash 2>&1
    $scan | ForEach-Object { $_ } | Out-File -FilePath (Join-Path $work "scan-output.txt") -Encoding UTF8
    $code = $LASTEXITCODE
    Check "rx_selftest --scan exits with 0" ($code -eq 0) ("exit code $code")
    $found = @{}
    foreach ($line in $scan) {
        $s = [string]$line
        if ($s.StartsWith("ENTRY`t")) { $f = $s.Split("`t"); $found[$f[1].ToLower()] = @{ size = [int64]$f[2]; cond = $f[3]; sha = $f[4] } }
        elseif ($s.StartsWith("summary") -or $s.StartsWith("scan_")) { Log $s }
    }

    # ---- 5. compare with what was known before deletion ----
    $okCount = 0; $deletedCount = 0
    foreach ($l in (Get-Content $expectFile)) {
        if ($l.StartsWith("#")) { continue }
        $f = $l.Split("`t")
        $path = $f[0]; $size = [int64]$f[1]; $sha = $f[2]; $deleted = ($f[3] -eq "1")
        $hit = $found[$path.ToLower()]
        if (-not $deleted) { Check ("kept file is not reported as deleted: " + $path) ($hit -eq $null) "reported as deleted"; continue }
        $deletedCount++
        if ($hit -eq $null) { Check ("deleted file found: " + $path) $false "not reported by the scan"; continue }
        $sizeOk = ($hit.size -eq $size)
        $hashOk = ($size -eq 0) -or ($hit.sha -eq $sha)
        if ($sizeOk -and $hashOk) { $okCount++ } else { Check ("recovered content identical: " + $path) $false ("size " + $hit.size + " vs " + $size + "; sha " + $hit.sha + " vs " + $sha + "; condition " + $hit.cond) }
    }
    Check ("all deleted files recovered byte-for-byte (" + $okCount + " of " + $deletedCount + ")") ($okCount -eq $deletedCount) "see failures above"
}
catch {
    Log ("ERROR: " + $_.Exception.Message)
    $script:failures++
}
finally {
    # ---- 6. always remove the disposable disk ----
    try { Run-Diskpart @("select vdisk file=`"$vhd`"", "detach vdisk") | Out-Null } catch { }
    if (-not $KeepVhd) { Start-Sleep -Seconds 1; Remove-Item -LiteralPath $vhd -Force -ErrorAction SilentlyContinue }
}

Log ""
if ($script:failures -eq 0) { Log "RESULT: PASS" } else { Log ("RESULT: FAIL (" + $script:failures + " problem(s))") }
Write-Host ("Report and scan output: " + $work) -ForegroundColor Cyan
exit $script:failures
