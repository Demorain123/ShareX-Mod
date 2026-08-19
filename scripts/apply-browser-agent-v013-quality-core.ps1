[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$stitcher = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentStreamingPngStitcher.cs"
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.3-core] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.3 core compatibility anchor missing: '$Marker' in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.3-core] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        $updated = $text.Replace($Old, $New)
        [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.3-core] applied: $Marker" -ForegroundColor Cyan
    }
}

# --- Capture session --------------------------------------------------------
Replace-One $session `
@'
        Action<string>? status,
        CancellationToken cancellationToken)
    {
'@ `
@'
        Action<string>? status,
        CancellationToken cancellationToken,
        BrowserAgentCaptureOptions? captureOptions = null)
    {
'@ `
    'BrowserAgentCaptureOptions? captureOptions = null'

Replace-One $session `
@'
        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
'@ `
@'
        BrowserAgentCaptureOptions options = (captureOptions ?? BrowserAgentCaptureOptions.Default).Normalize();
        safetyFrameLimit = Math.Clamp(safetyFrameLimit, 2, MaximumSafetyFrameLimit);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
'@ `
    'BrowserAgentCaptureOptions options = (captureOptions'

Replace-One $session `
@'
        var manifest = new BrowserAgentSessionManifest
        {
            StartedUtc = DateTime.UtcNow,
            SafetyFrameLimit = safetyFrameLimit
        };
'@ `
@'
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
    'RegionSelectionRequired = options.RequireRegionSelection'

Replace-One $session `
@'
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
@'
        status?.Invoke(options.RequireRegionSelection
            ? "Drag the exact Browser Assisted capture region in the web page (Esc cancels; Enter = full viewport)..."
            : "Using the full browser viewport...");
        JsonElement selection = await bridge.SendRequestAsync(
            "selectRegion",
            new { fullViewport = !options.RequireRegionSelection },
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        if (ReadBool(selection, "cancelled"))
        {
            throw new InvalidOperationException("Browser Agent region selection was cancelled before capture.");
        }
        if (!selection.TryGetProperty("captureRegion", out JsonElement selectedRegion))
        {
            throw new InvalidOperationException("Browser Agent did not return a capture region.");
        }
        manifest.CaptureRegionLeftCss = ReadDouble(selectedRegion, "left");
        manifest.CaptureRegionTopCss = ReadDouble(selectedRegion, "top");
        manifest.CaptureRegionWidthCss = ReadDouble(selectedRegion, "width");
        manifest.CaptureRegionHeightCss = ReadDouble(selectedRegion, "height");
        SaveManifest(manifestPath, manifest);

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
    '"selectRegion",'

Replace-One $session `
@'
                    new
                    {
                        stableWindowMs = StableWindowMs,
                        maxWaitMs = MaxStabilityWaitMs,
                        sampleMs = StabilitySampleMs,
                        overlapRatio = DefaultOverlapRatio,
                        hideFixed = sequence > 1
                    },
'@ `
@'
                    new
                    {
                        stableWindowMs = options.StableWindowMs,
                        maxWaitMs = Math.Max(MaxStabilityWaitMs, options.StableWindowMs * 6),
                        sampleMs = StabilitySampleMs,
                        overlapRatio = options.OverlapRatio,
                        hideFixed = sequence > 1
                    },
'@ `
    'overlapRatio = options.OverlapRatio'

Replace-One $session `
@'
                BrowserAgentFrameRecord record = await SaveNewFrameAsync(
                    sessionDirectory,
                    sequence,
                    response,
                    cancellationToken).ConfigureAwait(false);
'@ `
@'
                BrowserAgentFrameRecord record = await SaveNewFrameAsync(
                    sessionDirectory,
                    sequence,
                    response,
                    options.OverlapRatio,
                    cancellationToken).ConfigureAwait(false);
'@ `
    '                    options.OverlapRatio,'

Replace-One $session `
@'
    private static object StabilityPayload() => new
    {
        stableWindowMs = StableWindowMs,
        maxWaitMs = MaxStabilityWaitMs,
        sampleMs = StabilitySampleMs
    };
'@ `
@'
    private static object StabilityPayload(BrowserAgentCaptureOptions options) => new
    {
        stableWindowMs = options.StableWindowMs,
        maxWaitMs = Math.Max(MaxStabilityWaitMs, options.StableWindowMs * 6),
        sampleMs = StabilitySampleMs
    };
'@ `
    'StabilityPayload(BrowserAgentCaptureOptions options)'

Replace-One $session `
@'
    private async Task<BrowserAgentFrameRecord> SaveNewFrameAsync(
        string sessionDirectory,
        int sequence,
        JsonElement response,
        CancellationToken cancellationToken)
'@ `
@'
    private async Task<BrowserAgentFrameRecord> SaveNewFrameAsync(
        string sessionDirectory,
        int sequence,
        JsonElement response,
        double overlapRatio,
        CancellationToken cancellationToken)
'@ `
    '        double overlapRatio,'

Replace-One $session `
@'
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
@'
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
    'BrowserAgentCroppedFrame cropped = BrowserAgentFrameCropper.Crop'

Replace-One $session `
    '        double delta = Math.Max(1, Math.Floor(viewportHeight * (1 - DefaultOverlapRatio)));' `
    '        double delta = Math.Max(1, Math.Floor(viewportHeight * (1 - overlapRatio)));' `
    'viewportHeight * (1 - overlapRatio)'

Replace-One $session `
@'
            ViewportWidthCss = ReadDouble(before, "viewportWidth"),
            ViewportHeightCss = viewportHeight,
            DevicePixelRatio = ReadDouble(before, "devicePixelRatio"),
'@ `
@'
            ViewportWidthCss = cropped.Region.WidthCss,
            ViewportHeightCss = viewportHeight,
            CaptureRegionLeftCss = cropped.Region.LeftCss,
            CaptureRegionTopCss = cropped.Region.TopCss,
            CaptureRegionWidthCss = cropped.Region.WidthCss,
            CaptureRegionHeightCss = cropped.Region.HeightCss,
            DevicePixelRatio = ReadDouble(before, "devicePixelRatio"),
'@ `
    'CaptureRegionWidthCss = cropped.Region.WidthCss'

Replace-One $session `
@'
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        (int pixelWidth, int pixelHeight) = ReadPngDimensions(png);
        string framePath = Path.Combine(sessionDirectory, frame.FileName);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        JsonElement before = response.GetProperty("before");
        frame.ScrollYCss = ReadDouble(before, "scrollY");
'@ `
@'
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
    'BrowserAgentCroppedFrame cropped = BrowserAgentFrameCropper.Crop(png, response, before);'

# The crop marker above occurs twice after a successful first pass, so patch the
# recapture metrics with a dedicated marker that does not overlap SaveNewFrame.
Replace-One $session `
@'
        frame.ViewportWidthCss = ReadDouble(before, "viewportWidth");
        frame.ViewportHeightCss = ReadDouble(before, "viewportHeight");
        frame.DevicePixelRatio = ReadDouble(before, "devicePixelRatio");
'@ `
@'
        frame.ViewportWidthCss = cropped.Region.WidthCss;
        frame.ViewportHeightCss = cropped.Region.HeightCss;
        frame.CaptureRegionLeftCss = cropped.Region.LeftCss;
        frame.CaptureRegionTopCss = cropped.Region.TopCss;
        frame.CaptureRegionWidthCss = cropped.Region.WidthCss;
        frame.CaptureRegionHeightCss = cropped.Region.HeightCss;
        frame.DevicePixelRatio = ReadDouble(before, "devicePixelRatio");
'@ `
    'frame.CaptureRegionWidthCss = cropped.Region.WidthCss'

Replace-One $session `
@'
        current.OverlapPixels = check.OverlapPixels;
        current.OverlapStatus = check.Detail;
        return check;
'@ `
@'
        current.OverlapPixels = check.OverlapPixels;
        current.OverlapStatus = check.Detail;
        current.ExpectedDeltaPixels = check.ExpectedDeltaPixels;
        current.ResolvedDeltaPixels = check.ResolvedDeltaPixels;
        current.VisualDeltaOffsetPixels = check.VisualDeltaOffsetPixels;
        current.VisualAlignmentScore = check.AlignmentScore;
        current.VisualAlignmentConfidence = check.AlignmentConfidence;
        return check;
'@ `
    'current.ResolvedDeltaPixels = check.ResolvedDeltaPixels;'

if (-not $CheckOnly) {
    $text = [IO.File]::ReadAllText($session)
    $text = $text.Replace('LongCapture-BrowserAgent-v012-', 'LongCapture-BrowserAgent-v013-')
    $text = $text.Replace('Browser Agent v0.1.2 capture aborted', 'Browser Agent v0.1.3 capture aborted')
    $text = $text.Replace('Browser Agent v0.1.2 capture ended', 'Browser Agent v0.1.3 capture ended')
    [IO.File]::WriteAllText($session, $text, [Text.UTF8Encoding]::new($true))
}

# --- Stitcher: cumulative visual movement rather than absolute scrollY -------
Replace-One $stitcher `
@'
            previousScrollY = frame.ScrollYCss;
            starts[i] = Math.Max(0, (int)Math.Round(frame.ScrollYCss * scaleY));
            capturedBottom = Math.Max(capturedBottom, starts[i] + frame.PixelHeight);
'@ `
@'
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
                        $"Browser Agent frame {frame.Sequence} has invalid resolved movement {resolvedDelta}px (DOM fallback {fallbackDelta}px).");
                }
                starts[i] = starts[i - 1] + resolvedDelta;
            }
            capturedBottom = Math.Max(capturedBottom, starts[i] + frame.PixelHeight);
'@ `
    'int resolvedDelta = frame.ResolvedDeltaPixels > 0'

Replace-One $stitcher `
    '        bool completePage = frames[^1].AtBottom;' `
    '        bool completePage = frames[^1].AtBottom && frames[^1].EndConfirmed;' `
    'frames[^1].AtBottom && frames[^1].EndConfirmed'

# --- Extension: real F8 region picker + quality preload ----------------------
Replace-One $worker `
@'
let nativePort = null;
let target = null;
'@ `
@'
let nativePort = null;
let target = null;
let captureRegion = null;
'@ `
    'let captureRegion = null;'

Replace-One $worker `
@'
      case "preview":
        result = await previewCaptureRegion(payload);
        break;
      case "begin":
'@ `
@'
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
    'case "selectRegion":'

Replace-One $worker `
@'
async function beginCapture(payload) {
'@ `
@'
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

    const counterComplete = after.pageCounterTotal > 0 && after.pageCounterCurrent >= after.pageCounterTotal;
    if (end.confirmed === true && (counterComplete || after.pageCounterTotal <= 0)) {
      reachedEnd = true;
      latest = after;
      break;
    }
    if (end.confirmed === true && noGrowthRounds >= 2) {
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
    'async function preloadDynamicContent(payload)'

Replace-One $worker `
    '  const overlapRatio = clampNumber(payload.overlapRatio, 0.10, 0.45, 0.26);' `
    '  const overlapRatio = clampNumber(payload.overlapRatio, 0.20, 0.50, 0.32);' `
    '0.20, 0.50, 0.32'

Replace-One $worker `
    '  const delta = Math.max(1, Math.floor(captured.before.viewportHeight * (1 - overlapRatio)));' `
@'
  const captureHeight = Math.max(80, Number(captureRegion?.height || captured.before.viewportHeight));
  const delta = Math.max(1, Math.floor(captureHeight * (1 - overlapRatio)));
'@ `
    'const captureHeight = Math.max(80'

Replace-One $worker `
@'
    hiddenCount,
    captureStateChanged,
    pngDataUrl
'@ `
@'
    hiddenCount,
    captureStateChanged,
    captureRegion,
    pngDataUrl
'@ `
    '    captureRegion,'

Replace-One $worker `
@'
function showCapturePreview(durationMs) {
'@ `
@'
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
    help.textContent = "LongCapture · drag to select capture area · Esc cancels · Enter uses full viewport";
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
    const finish = value => {
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
      Object.assign(box.style, { left: `${left}px`, top: `${top}px`, width: `${width}px`, height: `${height}px` });
    });

    overlay.addEventListener("pointerup", event => {
      if (!dragging) return;
      dragging = false;
      event.preventDefault();
      if (!currentRect || currentRect.width < 80 || currentRect.height < 80) {
        box.style.display = "none";
        help.textContent = "Selection too small · drag at least 80 × 80 CSS px · Esc cancels";
        return;
      }
      finish({ cancelled: false, ...currentRect });
    });
  });
}

function showCapturePreview(durationMs) {
'@ `
    'function chooseCaptureRegion()'

if (-not $CheckOnly) {
    $text = [IO.File]::ReadAllText($worker)
    $text = $text.Replace('protocolVersion: "0.1.2"', 'protocolVersion: "0.1.3"')
    $text = $text.Replace('LongCapture Browser Agent v0.1.2 attached to this tab', 'LongCapture Browser Agent v0.1.3 attached to this tab')
    [IO.File]::WriteAllText($worker, $text, [Text.UTF8Encoding]::new($true))
}

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.3 quality core compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.3 region/preload/visual-geometry core applied." -ForegroundColor Green
}
