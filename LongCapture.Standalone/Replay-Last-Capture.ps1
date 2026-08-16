[CmdletBinding()]
param(
    [string]$Session,
    [switch]$DiscoverOnly
)

$ErrorActionPreference = "Stop"
$base = Split-Path -Parent $MyInvocation.MyCommand.Path

function Test-ReplayableSession {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $Path "raw-frames-v016") -PathType Container) -or
           (Test-Path -LiteralPath (Join-Path $Path "engine-evidence\raw-frames-v016") -PathType Container)
}

if ([string]::IsNullOrWhiteSpace($Session)) {
    Write-Host "Searching known LongCapture capture-session roots for the newest replayable raw-frame session..."

    # v0.1.8 scanned every sibling directory of the portable package. A perfectly legal directory
    # such as [Backup_TS_mainly]Chrome-... was then passed to wildcard-aware Test-Path/Get-ChildItem
    # and PowerShell treated [] as a wildcard character class. v0.1.9 searches only known roots and
    # every resolved filesystem path is consumed with -LiteralPath.
    $roots = @(
        (Join-Path $base "ShareX-Mod\CaptureSessions"),
        (Join-Path $env:LOCALAPPDATA "LongCapture\CaptureSessions"),
        (Join-Path $env:LOCALAPPDATA "ShareX-Mod\CaptureSessions")
    ) | Select-Object -Unique

    $candidates = foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Where-Object {
            Test-ReplayableSession -Path $_.FullName
        }
    }

    $picked = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($picked) { $Session = $picked.FullName }
}

if ([string]::IsNullOrWhiteSpace($Session)) {
    Write-Error "No replayable raw-frame capture session was found. v0.1.9 RC records replay frames automatically during the next real capture."
    exit 2
}

$Session = [IO.Path]::GetFullPath($Session.Trim('"'))
if (-not (Test-ReplayableSession -Path $Session)) {
    Write-Error "Replay session directory is missing replayable raw frames: $Session"
    exit 3
}

if ($DiscoverOnly) {
    Write-Host "Replayable session:"
    Write-Host $Session
    exit 0
}

$exe = Join-Path $base "LongCapture.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Write-Error "LongCapture.exe not found next to this script: $exe"
    exit 4
}

Write-Host "Replaying with v0.1.9 validated geometry + provisional-tail compositor:"
Write-Host $Session
& $exe --replay $Session
$rc = $LASTEXITCODE
if ($rc -ne 0) {
    Write-Error "Replay failed with exit code $rc. v0.1.9 remains fail-closed and will not manufacture geometry with the legacy mosaic matcher."
    exit $rc
}

Write-Host "Replay completed. See the session folder for LongCapture-Replay-v019-*.png and *.json."
exit 0
