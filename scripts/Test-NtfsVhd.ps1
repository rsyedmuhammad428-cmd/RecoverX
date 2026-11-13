<#
.SYNOPSIS
  End-to-end NTFS recovery test on a DISPOSABLE virtual disk. Creates a small VHD, formats it NTFS, writes files whose
  SHA-256 is known, deletes them, runs the RecoverX engine's Quick Scan and Deep Scan against the VHD, and compares the recovered bytes.
  Nothing on your real drives is touched: only the temporary VHD (deleted at the end) is scanned.

  Run from an ELEVATED PowerShell on each Windows version you want to test (Windows 7 SP1 / 10 / 11), after scripts\build-native.ps1.
  Works with Windows PowerShell 2.0+. Uses diskpart (present on all supported versions).

.PARAMETER NativeBin  Folder with x64\ and x86\ engine builds (default ..\native-bin).
.PARAMETER Letter     Drive letter to give the VHD (default: first free letter from Z downwards).
.PARAMETER KeepVhd    Keep the VHD file afterwards for inspection.

STATUS: NOT yet executed on any Windows version (needs an elevated session). Phase 4 adds a Deep Scan check: real JPEG/PNG/ZIP files made
  by Windows encoders are deleted and must be carved back byte-identical and rated intact.
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

    # ---- 2b. real-format files for Deep Scan, made by Windows' own encoders (GDI+ JPEG/PNG, .NET ZIP) ----
    $deepFile = Join-Path $work "deep-expected.txt"
    "# name`tsize`tsha256" | Out-File -FilePath $deepFile -Encoding UTF8
    $deepDir = Join-Path $root "deep"
    New-Item -ItemType Directory -Force -Path $deepDir | Out-Null
    Add-Type -AssemblyName System.Drawing
    $deepNames = @()
    foreach ($fmt in @(@("photo_deep.jpg", [Drawing.Imaging.ImageFormat]::Jpeg), @("image_deep.png", [Drawing.Imaging.ImageFormat]::Png))) {
        $bmp = New-Object Drawing.Bitmap 640, 480
        for ($y = 0; $y -lt 480; $y += 4) { for ($x = 0; $x -lt 640; $x += 4) { $bmp.SetPixel($x, $y, [Drawing.Color]::FromArgb(($x * 7) % 256, ($y * 3) % 256, ($x + $y) % 256)) } }
        $full = Join-Path $deepDir $fmt[0]
        $bmp.Save($full, $fmt[1]); $bmp.Dispose()
        $deepNames += $fmt[0]
    }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop       # .NET 4.5+; skipped on PowerShell 2.0
        $zsrc = Join-Path $work "zipsrc"; New-Item -ItemType Directory -Force -Path $zsrc | Out-Null
        for ($i = 0; $i -lt 5; $i++) { [IO.File]::WriteAllBytes((Join-Path $zsrc ("part" + $i + ".bin")), (Make-Bytes 40000 (20 + $i) $null)) }
        [IO.Compression.ZipFile]::CreateFromDirectory($zsrc, (Join-Path $deepDir "archive_deep.zip"))
        $deepNames += "archive_deep.zip"
    } catch { Log "ZIP creation skipped (needs .NET 4.5+ in PowerShell): $($_.Exception.Message)" }
    foreach ($n in $deepNames) {
        $b = [IO.File]::ReadAllBytes((Join-Path $deepDir $n))
        ($n + "`t" + $b.Length + "`t" + (Sha256Hex $b)) | Out-File -FilePath $deepFile -Append -Encoding UTF8
    }
    Log ("Created " + $deepNames.Count + " real-format files for Deep Scan")

    # ---- 3. permanent deletion (no Recycle Bin) ----
    foreach ($p in $plan) { if ($p[2]) { $full = Join-Path $root $p[0]; if (Test-Path $full) { Remove-Item -LiteralPath $full -Force } } }
    Remove-Item -LiteralPath (Join-Path $root "deleted_dir") -Recurse -Force
    Remove-Item -LiteralPath $deepDir -Recurse -Force
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

    # ---- 6. Deep Scan: the deleted real-format files must be found by signature in free space, byte-identical ----
    $carve = & $selftest --carve $Letter --hash 2>&1
    $carve | ForEach-Object { $_ } | Out-File -FilePath (Join-Path $work "carve-output.txt") -Encoding UTF8
    $ccode = $LASTEXITCODE
    Check "rx_selftest --carve exits with 0" ($ccode -eq 0) ("exit code $ccode")
    $carved = @{}
    foreach ($line in $carve) {
        $s = [string]$line
        if ($s.StartsWith("CARVED`t")) { $f = $s.Split("`t"); $carved[$f[6]] = @{ size = [int64]$f[2]; ext = $f[3]; cond = $f[4] } }
        elseif ($s.StartsWith("summary") -or $s.StartsWith("carve_")) { Log $s }
    }
    foreach ($l in (Get-Content $deepFile)) {
        if ($l.StartsWith("#")) { continue }
        $f = $l.Split("`t")
        $hit = $carved[$f[2]]
        Check ("Deep Scan recovered " + $f[0] + " byte-for-byte") ($null -ne $hit) "no carved file has the original SHA-256"
        if ($null -ne $hit) { Check ("Deep Scan rates " + $f[0] + " as intact") ($hit.cond -eq "VERIFIED") ("condition " + $hit.cond) }
    }
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
