[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$manager = Join-Path $repoRoot "ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs"
$text = [IO.File]::ReadAllText($manager)

$old = @'
                            if (modChromeSession != null && modV04.ChromeImageAppendixEnabled && modSegmentStore.HasParts)
                            {
                                await modChromeSession.ExportImageAppendixAsync(modSegmentStore.DirectoryPath);
                            }
'@
$new = @'
                            if (modChromeSession != null && modV04.ChromeImageAppendixEnabled && modSegmentStore.HasParts)
                            {
                                ShareXModChromeImageAppendixResult appendix =
                                    await modChromeSession.ExportImageAppendixAsync(modSegmentStore.DirectoryPath);

                                if (appendix != null && appendix.SavedCount > 0)
                                {
                                    int appendixWidth = Result?.Width ?? 0;
                                    if (ShareXModSegmentedOutputRegistry.TryGet(Result, out _, out int originalWidth, out _))
                                    {
                                        appendixWidth = originalWidth;
                                    }

                                    var appendixTail = ShareXModImageAppendixTail.Build(modSegmentStore.DirectoryPath, appendixWidth);
                                    Bitmap appendixPreview = modSegmentStore.AppendTailPartsAndCreatePreview(appendixTail);
                                    if (appendixPreview != null)
                                    {
                                        Result?.Dispose();
                                        Result = appendixPreview;
                                    }
                                }
                            }
'@
$marker = "ShareXModImageAppendixTail.Build"

if ($text.Contains($marker)) {
    Write-Host "[v0.4.1-post] appendix tail hook already present." -ForegroundColor DarkYellow
    exit 0
}
if (-not $text.Contains($old)) {
    throw "v0.4.1 appendix tail hook anchor not found."
}

$text = $text.Replace($old, $new)
[IO.File]::WriteAllText($manager, $text, [Text.UTF8Encoding]::new($true))
Write-Host "[v0.4.1-post] appended high-resolution image assets to long-capture tail." -ForegroundColor Cyan
