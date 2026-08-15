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
        Write-Host "[v0.6.1-hook] already present: $Marker" -ForegroundColor DarkYellow
        return
    }

    if (-not $text.Contains($Old)) {
        throw "v0.6.1 compatibility check failed: '$Marker' anchor not found."
    }

    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[v0.6.1-hook] applied: $Marker" -ForegroundColor Cyan
}

# A configured Capture Recipe is a first-class background capture mode. It runs before the
# generic Chrome full-page path because the recipe can contain partial ranges, page transitions
# and horizontal sweeps that would be destroyed by treating the page as one ordinary full page.
Replace-Literal `
    -Old @'
                    if (modV04.ChromeEnhancedEnabled && modV04.ChromeBackgroundCapture)
                    {
                        ShareXModChromeBackgroundCaptureResult chromeBackgroundResult =
                            await ShareXModChromeBackgroundCaptureEntry.TryCaptureAsync(
                                selectedWindow.Handle,
                                modV04,
                                () => stopRequested);
'@ `
    -New @'
                    if (modV04.ChromeEnhancedEnabled &&
                        modV04.CaptureRecipeAutomationEnabled &&
                        !string.IsNullOrWhiteSpace(modV04.CaptureRecipeReplayPath))
                    {
                        ShareXModChromeBackgroundCaptureResult recipeResult =
                            await ShareXModChromeRecipeCaptureEntry.TryCaptureAsync(
                                selectedWindow.Handle,
                                modV04,
                                () => stopRequested);

                        if (recipeResult != null)
                        {
                            Result?.Dispose();
                            Result = recipeResult.Preview;
                            status = ScrollingCaptureStatus.Successful;
                            return status;
                        }
                    }

                    if (modV04.ChromeEnhancedEnabled && modV04.ChromeBackgroundCapture)
                    {
                        ShareXModChromeBackgroundCaptureResult chromeBackgroundResult =
                            await ShareXModChromeBackgroundCaptureEntry.TryCaptureAsync(
                                selectedWindow.Handle,
                                modV04,
                                () => stopRequested);
'@ `
    -Marker "ShareXModChromeRecipeCaptureEntry.TryCaptureAsync"

Write-Host "ShareX-Mod v0.6.1 post hooks applied." -ForegroundColor Green
