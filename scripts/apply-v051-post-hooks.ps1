[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
if (-not (Test-Path -LiteralPath $manager)) {
    throw "ScrollingCaptureManager.cs not found."
}

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    $text = [IO.File]::ReadAllText($manager)

    if ($text.Contains($Marker)) {
        Write-Host "[v0.5.1-hook] already present: $Marker" -ForegroundColor DarkYellow
        return
    }

    if (-not $text.Contains($Old)) {
        throw "v0.5.1 compatibility check failed: '$Marker' anchor not found."
    }

    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.5.1-hook] applied: $Marker" -ForegroundColor Cyan
}

# Defer the capture-session completion until segmented output, verified local repairs and
# appendix processing have finished. This first transformation is intentionally kept exact so
# an upstream finalization change is still surfaced rather than silently patched in the wrong place.
Replace-Literal `
    -Old @'
                    if (modChromeSession != null)
                    {
                        await modChromeSession.FinalizeSemanticCaptureAsync();
                    }

                    modCaptureSession?.Complete(modEndReason, status, Result);
'@ `
    -New @'
                    if (modChromeSession != null)
                    {
                        await modChromeSession.FinalizeSemanticCaptureAsync();
                    }

                    // ShareXMod-v0.5.1-session-complete-deferred
'@ `
    -Marker "ShareXMod-v0.5.1-session-complete-deferred"

# v0.4.1/v0.4.2 already expand the image-appendix block before v0.5.1 is replayed. The old
# v0.5.1 hook tried to replace the pre-v0.4.1 block verbatim, which made the current overlay
# chain fail before compilation. Insert the repair stage relative to stable semantic anchors
# instead, while still failing closed if those anchors disappear upstream.
$text = [IO.File]::ReadAllText($manager)
$repairMarker = "await modChromeSession.ApplyVerifiedRepairsAsync(Result);"

if ($text.Contains($repairMarker)) {
    Write-Host "[v0.5.1-hook] already present: $repairMarker" -ForegroundColor DarkYellow
}
else {
    $appendixAnchor = "                            if (modChromeSession != null && modV04.ChromeImageAppendixEnabled && modSegmentStore.HasParts)"
    if (-not $text.Contains($appendixAnchor)) {
        throw "v0.5.1 compatibility check failed: appendix anchor not found after v0.4.1/v0.4.2 replay."
    }

    $repairBlock = @'
                        if (modChromeSession != null)
                        {
                            await modChromeSession.ApplyVerifiedRepairsAsync(Result);
                        }

'@

    $text = $text.Replace($appendixAnchor, $repairBlock + $appendixAnchor)

    $completeMarker = "modCaptureSession?.Complete(modEndReason, status, Result);"
    if (-not $text.Contains($completeMarker)) {
        $disposeAnchor = @'
                    finally
                    {
                        modSegmentStore?.Dispose();
'@
        if (-not $text.Contains($disposeAnchor)) {
            throw "v0.5.1 compatibility check failed: segmented-output disposal anchor not found."
        }

        $completeBlock = @'
                        modCaptureSession?.Complete(modEndReason, status, Result);
                    }
                    finally
                    {
                        modSegmentStore?.Dispose();
'@
        $text = $text.Replace($disposeAnchor, $completeBlock)
    }

    [IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.5.1-hook] applied: $repairMarker" -ForegroundColor Cyan
}

Write-Host "ShareX-Mod v0.5.1 post hooks applied." -ForegroundColor Green
