[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [Parameter(Mandatory=$true)][string]$Marker
    )
    if (-not (Test-Path -LiteralPath $Path)) { throw "Browser Agent v0.1.3 target not found: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.3] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.3 compatibility anchor not found: '$Marker' in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.3] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $text = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.3] applied: $Marker" -ForegroundColor Cyan
    }
}

$main = Join-Path $repoRoot "LongCapture.Standalone\MainForm.cs"
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$stitcher = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentStreamingPngStitcher.cs"
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"

# ---------------------------------------------------------------------------
# Main UI: responsive target strip + Browser-mode settings instead of a wall
# of disabled Normal-capture controls.
# ---------------------------------------------------------------------------
Replace-Literal -Path $main `
    -Old '    private readonly BrowserAgentIntegratedController browserAgentController = new();' `
    -New @'
    private readonly BrowserAgentIntegratedController browserAgentController = new();
    private readonly BrowserAgentUiModeAdapterV013 browserAgentUiAdapter = new();
    private TableLayoutPanel mainBody = null!;
'@ `
    -Marker 'BrowserAgentUiModeAdapterV013 browserAgentUiAdapter'

Replace-Literal -Path $main `
    -Old @'
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
'@ `
    -New @'
        mainBody = body;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
'@ `
    -Marker 'mainBody = body;'

Replace-Literal -Path $main `
    -Old '        targetStrip.Controls.Add(openLogsButton, 4, 0);' `
    -New @'
        targetStrip.Controls.Add(openLogsButton, 4, 0);
        BrowserAgentUiModeAdapterV013.ReflowTargetStrip(
            targetStrip,
            targetLabel,
            targetSelector,
            foregroundTargetButton,
            refreshTargetsButton,
            openLogsButton);
'@ `
    -Marker 'BrowserAgentUiModeAdapterV013.ReflowTargetStrip('

Replace-Literal -Path $main `
    -Old @'
        Controls.Add(body);
        Controls.Add(targetStrip);
'@ `
    -New @'
        BrowserAgentUiModeAdapterV013.Polish(this, body);
        Controls.Add(body);
        Controls.Add(targetStrip);
'@ `
    -Marker 'BrowserAgentUiModeAdapterV013.Polish(this, body);'

Replace-Literal -Path $main `
    -Old @'
        if (BrowserAgentSelected)
        {
            browserButton.Text = "Open Browser Agent folder";
'@ `
    -New @'
        if (BrowserAgentSelected)
        {
            browserAgentUiAdapter.SetMode(
                true, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,
                autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
            browserButton.Text = "Open Browser Agent folder";
'@ `
    -Marker 'browserAgentUiAdapter.SetMode('

Replace-Literal -Path $main `
    -Old @'
        targetSelector.Enabled = !captureBusy;
        refreshTargetsButton.Enabled = !captureBusy;
'@ `
    -New @'
        browserAgentUiAdapter.SetMode(
            false, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,
            autoScrollTop, wholeWindowCapture, debugCaptureUi, includeInternalDebugWindows);
        targetSelector.Enabled = !captureBusy;
        refreshTargetsButton.Enabled = !captureBusy;
'@ `
    -Marker 'false, mainBody, startDelay, scrollDelay, scrollAmount, scrollMethod,'

Replace-Literal -Path $main `
    -Old @'
            BrowserAgentStitchResult result = await browserAgentController.CaptureAsync(message =>
            {
'@ `
    -New @'
            BrowserAgentCaptureOptions browserOptions = browserAgentUiAdapter.BuildOptions(
                startDelay, scrollDelay, scrollAmount, autoScrollTop, wholeWindowCapture);
            LongCaptureLog.Info(
                $"Browser Agent v0.1.3 options startDelay={browserOptions.StartDelayMs} settle={browserOptions.StableWindowMs} overlap={browserOptions.OverlapRatio:P0} preload={browserOptions.PreloadDynamicContent} regionSelect={browserOptions.RequireRegionSelection}");
            BrowserAgentStitchResult result = await browserAgentController.CaptureAsync(browserOptions, message =>
            {
'@ `
    -Marker 'Browser Agent v0.1.3 options startDelay='

Replace-Literal -Path $main `
    -Old '            Hide();
            await Task.Delay(180);' `
    -New @'
            Hide();
            await Task.Delay(120);
'@ `
    -Marker 'await Task.Delay(120);'

# v0.1.2 disabled Normal scrolling controls unconditionally in Browser mode.
# v0.1.3 gives the relevant values Browser semantics; the adapter restores Normal values when leaving the mode.
Replace-Literal -Path $main `
    -Old '        startDelay.Enabled = enabled && !BrowserAgentSelected;' `
    -New '        startDelay.Enabled = enabled;' `
    -Marker 'startDelay.Enabled = enabled; // Browser Agent v0.1.3'

if (-not $CheckOnly) {
    $text = [IO.File]::ReadAllText($main)
    $text = $text.Replace('        startDelay.Enabled = enabled;`r`n', '        startDelay.Enabled = enabled; // Browser Agent v0.1.3`r`n')
    $text = $text.Replace('        scrollDelay.Enabled = enabled && !BrowserAgentSelected;', '        scrollDelay.Enabled = enabled;')
    $text = $text.Replace('        scrollAmount.Enabled = enabled && !BrowserAgentSelected;', '        scrollAmount.Enabled = enabled;')
    $text = $text.Replace('        autoScrollTop.Enabled = enabled && !BrowserAgentSelected;', '        autoScrollTop.Enabled = enabled;')
    [IO.File]::WriteAllText($main, $text, [Text.UTF8Encoding]::new($true))
}

# ---------------------------------------------------------------------------
# Capture session: F8 now waits for a real in-page region selection, optionally
# preloads dynamic content, crops every tab PNG to that region, and threads the
# user-selected settle/overlap values through the capture loop.
# ---------------------------------------------------------------------------
Replace-Literal -Path $session `
    -Old @'
        Action<string>? status,
        CancellationToken cancellationToken)
    {
'@ `
    -New @'
        Action<string>? status,
        CancellationToken cancellationToken,
        BrowserAgentCaptureOptions? captureOptions = null)
    {
'@ `
    -Marker 'BrowserAgentCaptureOptions? captureOptions = null'

Replace-Literal -Path $session `
    -Old @'
        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
'@ `
    -New @'
        BrowserAgentCaptureOptions options = (captureOptions ?? BrowserAgentCaptureOptions.Default).Normalize();
        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
'@ `
    -Marker 'BrowserAgentCaptureOptions options = (captureOptions'

Replace-Literal -Path $session `
    -Old @'
        var manifest = new BrowserAgentSessionManifest
        {
            StartedUtc = DateTime.UtcNow,
            SafetyFrameLimit = safetyFrameLimit
        };
'@ `
    -New @'
        var manifest = new BrowserAgentSessionManifest
        {
            StartedUtc = DateTime.UtcNow,
            SafetyFrameLimit = safetyFrameLimit,
            StartDelayMs = options.StartDelayMs,
            StableWindowMs = options.StableWindowMs,
            OverlapRatio = options.OverlapRatio,
            RegionSelectionRequired = options.RequireRegionSelection,
            PreloadEnabled = options.PreloadDynamicContent
        };
'@ `
    -Marker 'RegionSelectionRequired = options.RequireRegionSelection'

Replace-Literal -Path $session `
    -Old @'
        status?.Invoke("Showing the Browser Assisted capture region...");
        await bridge.SendRequestAsync(
            "preview",
            new { durationMs = PreviewDurationMs },
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        status?.Invoke("Preparing the active Chromium tab and waiting for DOM/layout stability...");
        JsonElement begin = await bridge.SendRequestAsync(
            "begin",
            StabilityPayload(),
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
'@ `
    -New @'
        status?.Invoke(options.RequireRegionSelection
            ? "Drag the exact Browser Assisted capture region in the web page (Esc cancels)..."
            : "Using the full browser viewport...");
        JsonElement selection = await bridge.SendRequestAsync(
            "selectRegion",
            new { fullViewport = !options.RequireRegionSelection },
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        if (ReadBool(selection, "cancelled"))
        {
            throw new OperationCanceledException("Browser Agent region selection was cancelled.", cancellationToken);
        }
        if (selection.TryGetProperty("captureRegion", out JsonElement selectedRegion))
        {
            manifest.CaptureRegionLeftCss = ReadDouble(selectedRegion, "left");
            manifest.CaptureRegionTopCss = ReadDouble(selectedRegion, "top");
            manifest.CaptureRegionWidthCss = ReadDouble(selectedRegion, "width");
            manifest.CaptureRegionHeightCss = ReadDouble(selectedRegion, "height");
            SaveManifest(manifestPath, manifest);
        }

        if (options.PreloadDynamicContent)
        {
            status?.Invoke("Quality preload: materializing lazy/dynamic content before the real capture...");
            JsonElement preload = await bridge.SendRequestAsync(
                "preload",
                new
                {
                    stableWindowMs = options.StableWindowMs,
                    maxWaitMs = Math.Max(MaxStabilityWaitMs, options.StableWindowMs * 6),
                    sampleMs = StabilitySampleMs,
                    maxDurationMs = options.PreloadMaxSeconds * 1000
                },
                TimeSpan.FromSeconds(options.PreloadMaxSeconds + 30),
                cancellationToken).ConfigureAwait(false);
            manifest.PreloadReachedEnd = ReadBool(preload, "reachedEnd");
            manifest.PreloadSteps = ReadInt(preload, "steps");
            manifest.PreloadDurationMs = ReadInt(preload, "durationMs");
            manifest.PreloadGrowthCss = ReadDoubleOrDefault(preload, "growthCss");
            manifest.PreloadPageCounterCurrent = ReadInt(preload, "pageCounterCurrent");
            manifest.PreloadPageCounterTotal = ReadInt(preload, "pageCounterTotal");
            SaveManifest(manifestPath, manifest);
        }

        if (options.StartDelayMs > 0)
        {
            status?.Invoke($"Starting in {options.StartDelayMs} ms...");
            await Task.Delay(options.StartDelayMs, cancellationToken).ConfigureAwait(false);
        }

        status?.Invoke("Preparing the active Chromium tab and waiting for DOM/layout stability...");
        JsonElement begin = await bridge.SendRequestAsync(
            "begin",
            StabilityPayload(options),
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
'@ `
    -Marker '"selectRegion",'

Replace-Literal -Path $session `
    -Old @'
                        stableWindowMs = StableWindowMs,
                        maxWaitMs = MaxStabilityWaitMs,
                        sampleMs = StabilitySampleMs,
                        overlapRatio = DefaultOverlapRatio,
'@ `
    -New @'
                        stableWindowMs = options.StableWindowMs,
                        maxWaitMs = Math.Max(MaxStabilityWaitMs, options.StableWindowMs * 6),
                        sampleMs = StabilitySampleMs,
                        overlapRatio = options.OverlapRatio,
'@ `
    -Marker 'overlapRatio = options.OverlapRatio'

Replace-Literal -Path $session `
    -Old @'
                    response,
                    cancellationToken).ConfigureAwait(false);
'@ `
    -New @'
                    response,
                    options.OverlapRatio,
                    cancellationToken).ConfigureAwait(false);
'@ `
    -Marker 'options.OverlapRatio,`r`n                    cancellationToken'

Replace-Literal -Path $session `
    -Old '    private static object StabilityPayload() => new' `
    -New '    private static object StabilityPayload(BrowserAgentCaptureOptions options) => new' `
    -Marker 'StabilityPayload(BrowserAgentCaptureOptions options)'

Replace-Literal -Path $session `
    -Old @'
        stableWindowMs = StableWindowMs,
        maxWaitMs = MaxStabilityWaitMs,
'@ `
    -New @'
        stableWindowMs = options.StableWindowMs,
        maxWaitMs = Math.Max(MaxStabilityWaitMs, options.StableWindowMs * 6),
'@ `
    -Marker 'maxWaitMs = Math.Max(MaxStabilityWaitMs, options.StableWindowMs * 6),'

Replace-Literal -Path $session `
    -Old @'
        int sequence,
        JsonElement response,
        CancellationToken cancellationToken)
'@ `
    -New @'
        int sequence,
        JsonElement response,
        double overlapRatio,
        CancellationToken cancellationToken)
'@ `
    -Marker 'double overlapRatio,`r`n        CancellationToken cancellationToken'

Replace-Literal -Path $session `
    -Old @'
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        (int pixelWidth, int pixelHeight) = ReadPngDimensions(png);
        string relativePath = Path.Combine("frames", $"frame-{sequence:0000}.png");
        string framePath = Path.Combine(sessionDirectory, relativePath);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        JsonElement before = response.GetProperty("before");
        JsonElement after = response.GetProperty("after");
        double viewportHeight = ReadDouble(before, "viewportHeight");
'@ `
    -New @'
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        JsonElement before = response.GetProperty("before");
        JsonElement after = response.GetProperty("after");
        BrowserAgentCroppedFrame cropped = BrowserAgentFrameCropper.Crop(png, response, before);
        png = cropped.Png;
        int pixelWidth = cropped.PixelWidth;
        int pixelHeight = cropped.PixelHeight;
        string relativePath = Path.Combine("frames", $"frame-{sequence:0000}.png");
        string framePath = Path.Combine(sessionDirectory, relativePath);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        double viewportHeight = cropped.Region.HeightCss;
'@ `
    -Marker 'BrowserAgentCroppedFrame cropped = BrowserAgentFrameCropper.Crop'

Replace-Literal -Path $session `
    -Old '        double delta = Math.Max(1, Math.Floor(viewportHeight * (1 - DefaultOverlapRatio)));' `
    -New '        double delta = Math.Max(1, Math.Floor(viewportHeight * (1 - overlapRatio)));' `
    -Marker 'viewportHeight * (1 - overlapRatio)'

Replace-Literal -Path $session `
    -Old @'
            ViewportWidthCss = ReadDouble(before, "viewportWidth"),
            ViewportHeightCss = viewportHeight,
'@ `
    -New @'
            ViewportWidthCss = cropped.Region.WidthCss,
            ViewportHeightCss = viewportHeight,
            CaptureRegionLeftCss = cropped.Region.LeftCss,
            CaptureRegionTopCss = cropped.Region.TopCss,
            CaptureRegionWidthCss = cropped.Region.WidthCss,
            CaptureRegionHeightCss = cropped.Region.HeightCss,
'@ `
    -Marker 'CaptureRegionWidthCss = cropped.Region.WidthCss'

Replace-Literal -Path $session `
    -Old @'
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        (int pixelWidth, int pixelHeight) = ReadPngDimensions(png);
        string framePath = Path.Combine(sessionDirectory, frame.FileName);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        JsonElement before = response.GetProperty("before");
        frame.ScrollYCss = ReadDouble(before, "scrollY");
'@ `
    -New @'
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        JsonElement before = response.GetProperty("before");
        BrowserAgentCroppedFrame cropped = BrowserAgentFrameCropper.Crop(png, response, before);
        png = cropped.Png;
        int pixelWidth = cropped.PixelWidth;
        int pixelHeight = cropped.PixelHeight;
        string framePath = Path.Combine(sessionDirectory, frame.FileName);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        frame.ScrollYCss = ReadDouble(before, "scrollY");
'@ `
    -Marker 'frame.CaptureRegionWidthCss = cropped.Region.WidthCss'

if (-not $CheckOnly) {
    $text = [IO.File]::ReadAllText($session)
    $needle = '        frame.ViewportWidthCss = ReadDouble(before, "viewportWidth");' + "`r`n" +
              '        frame.ViewportHeightCss = ReadDouble(before, "viewportHeight");'
    $replacement = '        frame.ViewportWidthCss = cropped.Region.WidthCss;' + "`r`n" +
                   '        frame.ViewportHeightCss = cropped.Region.HeightCss;' + "`r`n" +
                   '        frame.CaptureRegionLeftCss = cropped.Region.LeftCss;' + "`r`n" +
                   '        frame.CaptureRegionTopCss = cropped.Region.TopCss;' + "`r`n" +
                   '        frame.CaptureRegionWidthCss = cropped.Region.WidthCss;' + "`r`n" +
                   '        frame.CaptureRegionHeightCss = cropped.Region.HeightCss;'
    if (-not $text.Contains('frame.CaptureRegionWidthCss = cropped.Region.WidthCss')) {
        if (-not $text.Contains($needle)) { throw "Browser Agent v0.1.3 recapture crop anchor missing" }
        $text = $text.Replace($needle, $replacement)
    }
    [IO.File]::WriteAllText($session, $text, [Text.UTF8Encoding]::new($true))
}

Replace-Literal -Path $session `
    -Old @'
        current.OverlapStatus = check.Detail;
        return check;
'@ `
    -New @'
        current.OverlapStatus = check.Detail;
        current.ExpectedDeltaPixels = check.ExpectedDeltaPixels;
        current.ResolvedDeltaPixels = check.ResolvedDeltaPixels;
        current.VisualDeltaOffsetPixels = check.VisualDeltaOffsetPixels;
        current.VisualAlignmentScore = check.AlignmentScore;
        current.VisualAlignmentConfidence = check.AlignmentConfidence;
        return check;
'@ `
    -Marker 'current.ResolvedDeltaPixels = check.ResolvedDeltaPixels;'

if (-not $CheckOnly) {
    $text = [IO.File]::ReadAllText($session)
    $text = $text.Replace('LongCapture-BrowserAgent-v012-', 'LongCapture-BrowserAgent-v013-')
    $text = $text.Replace('Browser Agent v0.1.2 capture aborted', 'Browser Agent v0.1.3 capture aborted')
    $text = $text.Replace('Browser Agent v0.1.2 capture ended', 'Browser Agent v0.1.3 capture ended')
    [IO.File]::WriteAllText($session, $text, [Text.UTF8Encoding]::new($true))
}

# ---------------------------------------------------------------------------
# Stitch from cumulative PNG-verified movement. DOM scrollY is still the
# prediction and navigation source, but a small browser scroll-anchor/layout
# correction no longer creates a seam just because the absolute page geometry moved.
# ---------------------------------------------------------------------------
Replace-Literal -Path $stitcher `
    -Old @'
            previousScrollY = frame.ScrollYCss;
            starts[i] = Math.Max(0, (int)Math.Round(frame.ScrollYCss * scaleY));
            capturedBottom = Math.Max(capturedBottom, starts[i] + frame.PixelHeight);
'@ `
    -New @'
            previousScrollY = frame.ScrollYCss;
            if (i == 0)
            {
                starts[i] = 0;
            }
            else
            {
                BrowserAgentFrameRecord previousFrame = frames[i - 1];
                int fallbackDelta = (int)Math.Round((frame.ScrollYCss - previousFrame.ScrollYCss) * scaleY);
                int resolvedDelta = frame.ResolvedDeltaPixels > 0 ? frame.ResolvedDeltaPixels : fallbackDelta;
                if (resolvedDelta <= 0 || resolvedDelta >= frame.PixelHeight)
                {
                    throw new InvalidOperationException(
                        $"Browser Agent frame {frame.Sequence} has invalid resolved movement {resolvedDelta}px (fallback {fallbackDelta}px).");
                }
                starts[i] = starts[i - 1] + resolvedDelta;
            }
            capturedBottom = Math.Max(capturedBottom, starts[i] + frame.PixelHeight);
'@ `
    -Marker 'int resolvedDelta = frame.ResolvedDeltaPixels > 0'

Replace-Literal -Path $stitcher `
    -Old '        bool completePage = frames[^1].AtBottom;' `
    -New '        bool completePage = frames[^1].AtBottom && frames[^1].EndConfirmed;' `
    -Marker 'frames[^1].AtBottom && frames[^1].EndConfirmed'

# ---------------------------------------------------------------------------
# Extension: region picker + full-page preload + selected-region-aware step.
# Chrome scripting.executeScript awaits the injected Promise, so the desktop
# can block on the user's drag selection without another permission.
# ---------------------------------------------------------------------------
Replace-Literal -Path $worker `
    -Old @'
let nativePort = null;
let target = null;
'@ `
    -New @'
let nativePort = null;
let target = null;
let captureRegion = null;
'@ `
    -Marker 'let captureRegion = null;'

Replace-Literal -Path $worker `
    -Old @'
      case "preview":
        result = await previewCaptureRegion(payload);
        break;
      case "begin":
'@ `
    -New @'
      case "preview":
        result = await previewCaptureRegion(payload);
        break;
      case "selectRegion":
        result = await selectCaptureRegion(payload);
        break;
      case "preload":
        result = await preloadDynamicContent(payload);
        break;
      case "begin":
'@ `
    -Marker 'case "selectRegion":'

Replace-Literal -Path $worker `
    -Old @'
async function beginCapture(payload) {
'@ `
    -New @'
async function selectCaptureRegion(payload) {
  const state = await collectState();
  if (payload.fullViewport === true) {
    captureRegion = { left: 0, top: 0, width: state.viewportWidth, height: state.viewportHeight };
    return { cancelled: false, captureRegion, viewportWidth: state.viewportWidth, viewportHeight: state.viewportHeight };
  }

  const selected = await executeInTarget(chooseCaptureRegion);
  if (!selected || selected.cancelled === true) {
    captureRegion = null;
    return { cancelled: true };
  }

  captureRegion = {
    left: clampNumber(selected.left, 0, Math.max(0, state.viewportWidth - 1), 0),
    top: clampNumber(selected.top, 0, Math.max(0, state.viewportHeight - 1), 0),
    width: clampNumber(selected.width, 80, state.viewportWidth, state.viewportWidth),
    height: clampNumber(selected.height, 80, state.viewportHeight, state.viewportHeight)
  };
  captureRegion.width = Math.min(captureRegion.width, state.viewportWidth - captureRegion.left);
  captureRegion.height = Math.min(captureRegion.height, state.viewportHeight - captureRegion.top);
  return { cancelled: false, captureRegion, viewportWidth: state.viewportWidth, viewportHeight: state.viewportHeight };
}

async function preloadDynamicContent(payload) {
  const options = stabilityOptions(payload);
  const maxDurationMs = clampNumber(payload.maxDurationMs, 10000, 180000, 90000);
  const started = Date.now();
  const initial = await collectState();
  let latest = initial;
  let greatestHeight = initial.scrollHeight;
  let steps = 0;
  let noGrowthRounds = 0;
  let reachedEnd = false;

  while (Date.now() - started < maxDurationMs && steps < 120) {
    latest = await collectState();
    const maxY = Math.max(0, latest.scrollHeight - latest.viewportHeight);
    await executeInTarget(scrollDocumentToAbsolute, [maxY]);
    await waitForStabilityOnTarget({
      stableWindowMs: Math.max(650, Math.min(options.stableWindowMs, 1100)),
      maxWaitMs: Math.max(options.maxWaitMs, 7000),
      sampleMs: options.sampleMs
    });

    const end = await confirmDocumentEnd(options);
    steps++;
    const after = end.state || await collectState();
    const previousHeight = greatestHeight;
    greatestHeight = Math.max(greatestHeight, after.scrollHeight);
    const grew = greatestHeight > previousHeight + 2 || Number(end.growthCss || 0) > 2;
    noGrowthRounds = grew ? 0 : noGrowthRounds + 1;

    const counterComplete =
      after.pageCounterTotal > 0 && after.pageCounterCurrent >= after.pageCounterTotal;
    if (end.confirmed === true && (counterComplete || after.pageCounterTotal <= 0)) {
      reachedEnd = true;
      latest = after;
      break;
    }

    if (end.confirmed === true && noGrowthRounds >= 2) {
      // A site may expose a coarse counter that never reaches its nominal total.
      // Stop preloading rather than looping forever; the real capture keeps the
      // normal lazy-boundary/end-confirmation guards enabled.
      latest = after;
      break;
    }
  }

  latest = await collectState();
  await executeInTarget(scrollDocumentToAbsolute, [0]);
  await waitForStabilityOnTarget(options);
  return {
    reachedEnd,
    steps,
    durationMs: Date.now() - started,
    growthCss: Math.max(0, greatestHeight - initial.scrollHeight),
    pageCounterCurrent: latest.pageCounterCurrent || 0,
    pageCounterTotal: latest.pageCounterTotal || 0,
    finalScrollHeight: greatestHeight
  };
}

async function beginCapture(payload) {
'@ `
    -Marker 'async function preloadDynamicContent(payload)'

Replace-Literal -Path $worker `
    -Old '  const overlapRatio = clampNumber(payload.overlapRatio, 0.10, 0.45, 0.26);' `
    -New '  const overlapRatio = clampNumber(payload.overlapRatio, 0.20, 0.50, 0.32);' `
    -Marker '0.20, 0.50, 0.32'

Replace-Literal -Path $worker `
    -Old '  const delta = Math.max(1, Math.floor(captured.before.viewportHeight * (1 - overlapRatio)));' `
    -New '  const captureHeight = Math.max(80, Number(captureRegion?.height || captured.before.viewportHeight));
  const delta = Math.max(1, Math.floor(captureHeight * (1 - overlapRatio)));' `
    -Marker 'const captureHeight = Math.max(80'

Replace-Literal -Path $worker `
    -Old @'
    hiddenCount,
    captureStateChanged,
    pngDataUrl
'@ `
    -New @'
    hiddenCount,
    captureStateChanged,
    captureRegion,
    pngDataUrl
'@ `
    -Marker '    captureRegion,`r`n    pngDataUrl'

Replace-Literal -Path $worker `
    -Old @'
function showCapturePreview(durationMs) {
'@ `
    -New @'
function chooseCaptureRegion() {
  return new Promise(resolve => {
    const marker = "data-longcapture-browser-agent-region-picker";
    document.querySelector(`[${marker}="1"]`)?.remove();

    const overlay = document.createElement("div");
    overlay.setAttribute(marker, "1");
    Object.assign(overlay.style, {
      position: "fixed",
      inset: "0",
      zIndex: "2147483647",
      cursor: "crosshair",
      background: "rgba(9, 14, 24, 0.12)",
      userSelect: "none",
      touchAction: "none"
    });

    const help = document.createElement("div");
    help.textContent = "LongCapture · Drag to select capture area · Esc cancels · Enter uses full viewport";
    Object.assign(help.style, {
      position: "absolute",
      left: "50%",
      top: "16px",
      transform: "translateX(-50%)",
      padding: "9px 14px",
      borderRadius: "10px",
      color: "#fff",
      background: "rgba(20, 24, 32, 0.88)",
      font: "600 13px system-ui, sans-serif",
      boxShadow: "0 8px 28px rgba(0,0,0,.22)",
      pointerEvents: "none"
    });

    const box = document.createElement("div");
    Object.assign(box.style, {
      position: "absolute",
      display: "none",
      border: "2px solid #1687ff",
      background: "rgba(22, 135, 255, 0.10)",
      boxShadow: "0 0 0 99999px rgba(9, 14, 24, 0.30)",
      pointerEvents: "none"
    });
    overlay.append(help, box);
    (document.documentElement || document.body).appendChild(overlay);

    let startX = 0;
    let startY = 0;
    let currentRect = null;
    let dragging = false;

    const cleanup = () => {
      window.removeEventListener("keydown", onKey, true);
      try { overlay.remove(); } catch (_) { }
    };
    const finish = (value) => {
      cleanup();
      resolve(value);
    };
    const onKey = event => {
      if (event.key === "Escape") {
        event.preventDefault();
        finish({ cancelled: true });
      } else if (event.key === "Enter") {
        event.preventDefault();
        finish({ cancelled: false, left: 0, top: 0, width: window.innerWidth, height: window.innerHeight });
      }
    };
    window.addEventListener("keydown", onKey, true);

    overlay.addEventListener("pointerdown", event => {
      event.preventDefault();
      dragging = true;
      startX = Math.max(0, Math.min(window.innerWidth, event.clientX));
      startY = Math.max(0, Math.min(window.innerHeight, event.clientY));
      currentRect = { left: startX, top: startY, width: 0, height: 0 };
      box.style.display = "block";
      try { overlay.setPointerCapture(event.pointerId); } catch (_) { }
    });

    overlay.addEventListener("pointermove", event => {
      if (!dragging) return;
      const x = Math.max(0, Math.min(window.innerWidth, event.clientX));
      const y = Math.max(0, Math.min(window.innerHeight, event.clientY));
      const left = Math.min(startX, x);
      const top = Math.min(startY, y);
      const width = Math.abs(x - startX);
      const height = Math.abs(y - startY);
      currentRect = { left, top, width, height };
      Object.assign(box.style, {
        left: `${left}px`, top: `${top}px`, width: `${width}px`, height: `${height}px`
      });
    });

    overlay.addEventListener("pointerup", event => {
      if (!dragging) return;
      dragging = false;
      event.preventDefault();
      if (!currentRect || currentRect.width < 80 || currentRect.height < 80) {
        box.style.display = "none";
        help.textContent = "Selection is too small · drag at least 80 × 80 CSS px · Esc cancels";
        return;
      }
      finish({ cancelled: false, ...currentRect });
    });
  });
}

function showCapturePreview(durationMs) {
'@ `
    -Marker 'function chooseCaptureRegion()'

if (-not $CheckOnly) {
    $text = [IO.File]::ReadAllText($worker)
    $text = $text.Replace('protocolVersion: "0.1.2"', 'protocolVersion: "0.1.3"')
    $text = $text.Replace('LongCapture Browser Agent v0.1.2 attached to this tab', 'LongCapture Browser Agent v0.1.3 attached to this tab')
    [IO.File]::WriteAllText($worker, $text, [Text.UTF8Encoding]::new($true))
}

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.3 quality/UI/region compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.3 quality/UI/region overlay applied." -ForegroundColor Green
}
