[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "apply-v04-hooks.ps1"
$temp = Join-Path $env:TEMP ("sharex-mod-v04-hooks-" + [Guid]::NewGuid().ToString("N") + ".ps1")

function Replace-GeneratedLiteral {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[v0.4.1-hook] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "v0.4.1 generated hook anchor not found: $Marker"
    }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.4.1-hook] applied: $Marker" -ForegroundColor Cyan
}

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
            Write-Host "[v0.4.1-hook] upgraded segmented preview result handler." -ForegroundColor Cyan
        }
        elseif (-not $windowText.Contains($new)) {
            throw "v0.4.1 segmented preview hook not found after applying overlay hooks."
        }

        $manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"

        # Match only against the central moving-content band while robust mode is enabled.
        # This keeps fixed side controls/timelines (for example Discourse's Back button) from
        # becoming false anchors without cropping them out of the captured output itself.
        Replace-GeneratedLiteral -Path $manager `
            -Old "            int ignoreSideOffset = Math.Max(50, currentImage.Width / 20);" `
            -New "            int ignoreSideOffset = Math.Max(50, currentImage.Width / (modRobustSession != null ? 4 : 20));" `
            -Marker "currentImage.Width / (modRobustSession != null ? 4 : 20)"

        # Upstream accepts even a one-row exact match. On browser pages a blank/static row can
        # therefore produce a catastrophic seam at the very beginning of a long capture.
        # Robust mode requires a short run of exact rows; otherwise the tolerant overlay matcher
        # gets the frame instead of trusting a weak primary match.
        Replace-GeneratedLiteral -Path $manager `
            -Old "            if (matchCount > 0)" `
            -New "            if (matchCount > 0 && (modRobustSession == null || matchCount >= Math.Max(6, currentImage.Height / 160)))" `
            -Marker "matchCount >= Math.Max(6, currentImage.Height / 160)"
    }

    exit 0
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}
