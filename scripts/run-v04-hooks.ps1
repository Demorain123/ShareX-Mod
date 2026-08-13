[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "apply-v04-hooks.ps1"
$temp = Join-Path $env:TEMP ("sharex-mod-v04-hooks-" + [Guid]::NewGuid().ToString("N") + ".ps1")

try {
    $text = [IO.File]::ReadAllText($source)
    $bad = '-Marker "string modEndReason = stopRequested ? \"manual-stop\" : \"capture-ended\";"'
    $good = '-Marker "modEndReason"'
    if ($text.Contains($bad)) {
        $text = $text.Replace($bad, $good)
    }
    [IO.File]::WriteAllText($temp, $text, [Text.UTF8Encoding]::new($false))
    if ($CheckOnly) { & pwsh -NoProfile -File $temp -CheckOnly }
    else { & pwsh -NoProfile -File $temp }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not $CheckOnly) {
        $repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
        $window = Join-Path $repoRoot "ShareX.ScreenCaptureLib\Presentation\ScrollingCapture\ScrollingCaptureWindow.axaml.cs"
        $windowText = [IO.File]::ReadAllText($window)
        $old = "await LoadShareXModImageAsync(_service.Result);"
        $new = "await LoadShareXModResultAsync(_service.Result);"
        if ($windowText.Contains($old)) {
            $windowText = $windowText.Replace($old, $new)
            [IO.File]::WriteAllText($window, $windowText, [Text.UTF8Encoding]::new($true))
            Write-Host "[v0.4-hook] upgraded segmented preview result handler." -ForegroundColor Cyan
        }
        elseif (-not $windowText.Contains($new)) {
            throw "v0.4 segmented preview hook not found after applying overlay hooks."
        }
    }

    exit 0
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}
