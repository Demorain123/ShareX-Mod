[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Update-RequiredLiteral {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )

    $fullPath = Join-Path $repoRoot $Path
    if (-not (Test-Path -LiteralPath $fullPath)) { throw "Hook target not found: $Path" }
    $text = [IO.File]::ReadAllText($fullPath)

    if ($text.Contains($Marker)) {
        Write-Host "[v0.4-hook] already present: $Path :: $Marker" -ForegroundColor DarkYellow
        return
    }

    if (-not $text.Contains($Old)) {
        throw "Hook compatibility check failed: '$Marker' anchor not found in $Path. Upstream changed near this hook point."
    }

    Write-Host "[v0.4-hook] compatible: $Path :: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $updated = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($fullPath, $updated, [Text.UTF8Encoding]::new($true))
        Write-Host "[v0.4-hook] applied: $Path :: $Marker" -ForegroundColor Cyan
    }
}

# 1) Import the overlay sources without copying them into upstream project folders.
$directoryTargets = "Directory.build.targets"
$importMarker = "ShareX.ScreenCaptureLib.Mod.targets"
$importBlock = @'
  <Import Project="$(MSBuildThisFileDirectory)mod-overlay\build\ShareX.ScreenCaptureLib.Mod.targets"
          Condition="'$(MSBuildProjectName)' == 'ShareX.ScreenCaptureLib' and Exists('$(MSBuildThisFileDirectory)mod-overlay\build\ShareX.ScreenCaptureLib.Mod.targets')" />
</Project>
'@
Update-RequiredLiteral -Path $directoryTargets -Old "</Project>" -New $importBlock -Marker $importMarker

$manager = "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"

# 2) One field is the entire persistent connection from upstream matcher -> robust overlay.
Update-RequiredLiteral -Path $manager `
    -Old "        private Rectangle selectedRectangle;" `
    -New @'
        private Rectangle selectedRectangle;
        private ShareXModRobustScrollingSession modRobustSession;
'@ `
    -Marker "private ShareXModRobustScrollingSession modRobustSession;"

# 3) Session creation stays immediately after the original Reset().
Update-RequiredLiteral -Path $manager `
    -Old @'
                Reset();

                ScrollingCaptureRegionWindow regionWindow = null;
'@ `
    -New @'
                Reset();

                ShareXModV04Settings modV04 = ShareXModV04Settings.Load();
                modRobustSession = ShareXModRobustScrollingSession.TryCreate(selectedRectangle, Options);
                ShareXModSegmentStore modSegmentStore = ShareXModSegmentStore.TryCreate(modV04);
                ShareXModChromeEnhancedSession modChromeSession = null;

                ScrollingCaptureRegionWindow regionWindow = null;
'@ `
    -Marker "ShareXModV04Settings modV04 = ShareXModV04Settings.Load();"

# 4) Chrome Enhanced is opportunistic. Failure falls back to generic ShareX capture.
Update-RequiredLiteral -Path $manager `
    -Old @'
                    await Task.Delay(Options.StartDelay);

                    if (Options.AutoScrollTop)
'@ `
    -New @'
                    await Task.Delay(Options.StartDelay);

                    if (modV04.ChromeEnhancedEnabled)
                    {
                        try
                        {
                            modChromeSession = await ShareXModChromeEnhancedSession.TryCreateAsync(selectedWindow.Handle, modV04);
                        }
                        catch
                        {
                            modChromeSession = null;
                        }
                    }

                    if (Options.AutoScrollTop)
'@ `
    -Marker "modChromeSession = await ShareXModChromeEnhancedSession.TryCreateAsync"

# 5) Observe every raw frame, and don't mistake lazy-load pauses for the end when robust manual-stop mode is enabled.
Update-RequiredLiteral -Path $manager `
    -Old @'
                        lastScreenshot = screenshot.CaptureRectangle(selectedRectangle);

                        if (CompareLastTwoImages())
                        {
                            break;
                        }
'@ `
    -New @'
                        lastScreenshot = screenshot.CaptureRectangle(selectedRectangle);
                        modRobustSession?.OnFrameCaptured(lastScreenshot);

                        if (CompareLastTwoImages())
                        {
                            if (modRobustSession?.ShouldStopOnUnchangedFrame() ?? true)
                            {
                                break;
                            }
                        }
                        else
                        {
                            modRobustSession?.OnChangedFrame();
                        }
'@ `
    -Marker "modRobustSession?.OnFrameCaptured(lastScreenshot);"

# 6) Primary matcher -> robust fallback -> bounded smart segment store.
Update-RequiredLiteral -Path $manager `
    -Old @'
                            Bitmap newResult = await CombineImagesAsync(Result, lastScreenshot);

                            if (newResult != null)
                            {
                                Result?.Dispose();
                                Result = newResult;
                            }
                            else
                            {
                                break;
                            }
'@ `
    -New @'
                            Bitmap newResult = await CombineImagesAsync(Result, lastScreenshot);
                            bool fallbackCombined = false;

                            if (newResult == null && modRobustSession != null)
                            {
                                newResult = modRobustSession.TryFallbackCombine(Result, previousScreenshot, lastScreenshot);
                                fallbackCombined = newResult != null;
                            }

                            if (newResult != null)
                            {
                                if (!fallbackCombined)
                                {
                                    modRobustSession?.OnPrimaryCombineSuccess();
                                }

                                Result?.Dispose();
                                Result = newResult;

                                if (modSegmentStore != null)
                                {
                                    Bitmap reduced = modSegmentStore.SegmentIfNeeded(Result);
                                    if (reduced != null)
                                    {
                                        Result.Dispose();
                                        Result = reduced;
                                    }
                                }
                            }
                            else if (!(modRobustSession?.ShouldContinueAfterCombineFailure() ?? false))
                            {
                                break;
                            }
'@ `
    -Marker "bool fallbackCombined = false;"

# 7) Replace fixed-delay-only behavior with visual settling when requested.
Update-RequiredLiteral -Path $manager `
    -Old @'
                        int delay = Options.ScrollDelay - (int)timer.ElapsedMilliseconds;

                        if (delay > 0)
                        {
                            await Task.Delay(delay);
                        }
'@ `
    -New @'
                        if (modV04.AdaptiveSettleEnabled)
                        {
                            await ShareXModAdaptiveSettle.WaitAsync(
                                () => screenshot.CaptureRectangle(selectedRectangle),
                                modV04,
                                Options.ScrollDelay);
                        }
                        else
                        {
                            int delay = Options.ScrollDelay - (int)timer.ElapsedMilliseconds;

                            if (delay > 0)
                            {
                                await Task.Delay(delay);
                            }
                        }
'@ `
    -Marker "await ShareXModAdaptiveSettle.WaitAsync("

# 8) Finalize segmented capture + optional Chrome image appendix before page state is restored.
Update-RequiredLiteral -Path $manager `
    -Old @'
                finally
                {
                    regionWindow?.Close();

                    Reset(true);
                    IsCapturing = false;
                }
'@ `
    -New @'
                finally
                {
                    regionWindow?.Close();

                    string modEndReason = stopRequested ? "manual-stop" : "capture-ended";
                    modRobustSession?.Complete(modEndReason, status, Result);

                    try
                    {
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
                    }
                    finally
                    {
                        modSegmentStore?.Dispose();
                        if (modChromeSession != null)
                        {
                            await modChromeSession.DisposeAsync();
                        }
                        modRobustSession?.Dispose();
                        modRobustSession = null;
                    }

                    Reset(true);
                    IsCapturing = false;
                }
'@ `
    -Marker "string modEndReason = stopRequested ? \"manual-stop\" : \"capture-ended\";"

# 9) In Robust mode, never reuse stale upstream best-guess offsets. Exact upstream match or overlay fallback only.
Update-RequiredLiteral -Path $manager `
    -Old "            if (matchCount == 0 && bestMatchCount > 0)" `
    -New "            if (matchCount == 0 && bestMatchCount > 0 && modRobustSession == null)" `
    -Marker "bestMatchCount > 0 && modRobustSession == null"

$window = "ShareX.ScreenCaptureLib\Presentation\ScrollingCapture\ScrollingCaptureWindow.axaml.cs"

# 10) Keep the proven v0.3 GDI+ oversized safe-preview path.
Update-RequiredLiteral -Path $window `
    -Old "            LoadImage(_service.Result);" `
    -New "            await LoadShareXModImageAsync(_service.Result);" `
    -Marker "await LoadShareXModImageAsync(_service.Result);"

Update-RequiredLiteral -Path $window `
    -Old "        if (_service.Options.AutoUpload)" `
    -New "        if (_service.Options.AutoUpload && !ShareXModOversizedImageSupport.IsOversized(_service.Result))" `
    -Marker "!ShareXModOversizedImageSupport.IsOversized(_service.Result)"

if ($CheckOnly) {
    Write-Host "ShareX-Mod v0.4 hook compatibility check passed." -ForegroundColor Green
} else {
    Write-Host "ShareX-Mod v0.4 hooks applied." -ForegroundColor Green
}
