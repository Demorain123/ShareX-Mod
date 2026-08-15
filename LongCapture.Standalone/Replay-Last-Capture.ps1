[CmdletBinding()]
param([string]$Session)

$ErrorActionPreference = "Stop"
$base = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($Session)) {
    Write-Host "Searching LongCapture capture-session roots for the newest replayable raw-frame session..."
    $parent = Split-Path $base -Parent
    $roots = @(
        (Join-Path $base "ShareX-Mod\CaptureSessions"),
        (Join-Path $env:LOCALAPPDATA "ShareX-Mod\CaptureSessions")
    )
    if (Test-Path $parent) {
        Get-ChildItem $parent -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $roots += Join-Path $_.FullName "ShareX-Mod\CaptureSessions"
        }
    }

    $candidates = foreach ($root in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object {
            (Test-Path (Join-Path $_.FullName "raw-frames-v016")) -or
            (Test-Path (Join-Path $_.FullName "engine-evidence\raw-frames-v016"))
        }
    }
    $picked = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($picked) { $Session = $picked.FullName }
}

if ([string]::IsNullOrWhiteSpace($Session)) {
    Write-Error "No replayable raw-frame capture session was found. v0.1.8 RC records replay frames automatically during the next real capture."
    exit 2
}

$Session = [IO.Path]::GetFullPath($Session.Trim('"'))
if (-not (Test-Path -LiteralPath $Session -PathType Container)) {
    Write-Error "Replay session directory does not exist: $Session"
    exit 3
}

$exe = Join-Path $base "LongCapture.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Error "LongCapture.exe not found next to this script: $exe"
    exit 4
}

Write-Host "Replaying with v0.1.8 trust-split compositor:"
Write-Host $Session
& $exe --replay $Session
$rc = $LASTEXITCODE
if ($rc -ne 0) {
    Write-Error "Replay failed with exit code $rc. The v0.1.8 path is fail-closed and will not manufacture a transition with a legacy mosaic matcher."
    exit $rc
}

Write-Host "Replay completed. See the session folder for LongCapture-Replay-v018-*.png and *.json."
exit 0
