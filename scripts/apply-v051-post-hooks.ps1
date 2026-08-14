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

Replace-Literal `
    -Old @'
                        if (modSegmentStore != null)
                        {
                            Bitmap preview = modSegmentStore.FinalizeAndCreatePreview(Result);
                            if (preview != null)
                            {
                                Result?.Dispose();
                                Result = preview;
                            }

                            if (modChromeSession != null && modV04.ChromeImageAppendixEnabled && modSegmentStore.HasParts)
                            {
                                await modChromeSession.ExportImageAppendixAsync(modSegmentStore.DirectoryPath);
                            }
                        }
'@ `
    -New @'
                        if (modSegmentStore != null)
                        {
                            Bitmap preview = modSegmentStore.FinalizeAndCreatePreview(Result);
                            if (preview != null)
                            {
                                Result?.Dispose();
                                Result = preview;
                            }
                        }

                        if (modChromeSession != null)
                        {
                            await modChromeSession.ApplyVerifiedRepairsAsync(Result);
                        }

                        if (modSegmentStore != null &&
                            modChromeSession != null &&
                            modV04.ChromeImageAppendixEnabled &&
                            modSegmentStore.HasParts)
                        {
                            await modChromeSession.ExportImageAppendixAsync(modSegmentStore.DirectoryPath);
                        }

                        modCaptureSession?.Complete(modEndReason, status, Result);
'@ `
    -Marker "await modChromeSession.ApplyVerifiedRepairsAsync(Result);"

Write-Host "ShareX-Mod v0.5.1 post hooks applied." -ForegroundColor Green
