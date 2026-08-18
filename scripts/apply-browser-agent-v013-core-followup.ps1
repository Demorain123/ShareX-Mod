[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$text = [IO.File]::ReadAllText($session)
$marker = '// v0.1.3 recapture selected-region crop'
if ($text.Contains($marker)) {
    Write-Host "[BrowserAgent-v0.1.3-followup] recapture crop already present." -ForegroundColor DarkYellow
    exit 0
}

$old = @'
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        (int pixelWidth, int pixelHeight) = ReadPngDimensions(png);
        string framePath = Path.Combine(sessionDirectory, frame.FileName);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        JsonElement before = response.GetProperty("before");
        frame.ScrollYCss = ReadDouble(before, "scrollY");
'@
$new = @'
        string pngDataUrl = ReadRequiredString(response, "pngDataUrl");
        byte[] png = DecodePngDataUrl(pngDataUrl);
        JsonElement before = response.GetProperty("before");
        BrowserAgentCroppedFrame cropped = BrowserAgentFrameCropper.Crop(png, response, before); // v0.1.3 recapture selected-region crop
        png = cropped.Png;
        int pixelWidth = cropped.PixelWidth;
        int pixelHeight = cropped.PixelHeight;
        string framePath = Path.Combine(sessionDirectory, frame.FileName);
        await File.WriteAllBytesAsync(framePath, png, cancellationToken).ConfigureAwait(false);

        frame.ScrollYCss = ReadDouble(before, "scrollY");
'@

if (-not $text.Contains($old)) {
    throw "Browser Agent v0.1.3 recapture crop anchor missing."
}
Write-Host "[BrowserAgent-v0.1.3-followup] recapture crop compatible." -ForegroundColor Green
if (-not $CheckOnly) {
    $text = $text.Replace($old, $new)
    [IO.File]::WriteAllText($session, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "[BrowserAgent-v0.1.3-followup] recapture crop applied." -ForegroundColor Cyan
}
