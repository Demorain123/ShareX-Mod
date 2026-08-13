$ErrorActionPreference = 'Stop'
$managerPath = Join-Path $PSScriptRoot '..\ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs'
$text = Get-Content $managerPath -Raw

function Replace-Anchor([string]$oldText, [string]$newText) {
    if (-not $script:text.Contains($oldText)) { throw 'ShareX-Mod v0.4 hook anchor mismatch' }
    $script:text = $script:text.Replace($oldText, $newText)
}

Replace-Anchor '                ShareXModV04Settings v04Settings = ShareXModV04Settings.Load();' @'
                ShareXModV04Settings v04Settings = ShareXModV04Settings.Load();
                ShareXModSegmentStore segmentStore = robustSession != null ? ShareXModSegmentStore.TryCreate(v04Settings) : null;
                ShareXModChromeEnhancedSession chromeSession = null;
'@

Replace-Anchor @'
                    await Task.Delay(Options.StartDelay);

                    if (Options.AutoScrollTop)
'@ @'
                    await Task.Delay(Options.StartDelay);

                    if (robustSession != null && v04Settings.ChromeEnhancedEnabled)
                    {
                        chromeSession = await ShareXModChromeEnhancedSession.TryCreateAsync(selectedWindow.Handle, v04Settings);
                    }

                    if (Options.AutoScrollTop)
'@

Replace-Anchor @'
                        if (stopRequested)
                        {
                            break;
                        }
'@ @'
                        Bitmap segmentedTail = segmentStore?.SegmentIfNeeded(Result);
                        if (segmentedTail != null)
                        {
                            Result?.Dispose();
                            Result = segmentedTail;
                        }

                        if (stopRequested)
                        {
                            break;
                        }
'@

Replace-Anchor @'
                    robustSession?.Dispose();
                    robustSession = null;

                    Reset(true);
'@ @'
                    robustSession?.Dispose();
                    robustSession = null;

                    if (segmentStore?.HasParts == true)
                    {
                        Bitmap preview = segmentStore.FinalizeAndCreatePreview(Result);
                        if (preview != null)
                        {
                            Result?.Dispose();
                            Result = preview;
                        }
                    }
                    segmentStore?.Dispose();
                    if (chromeSession != null) await chromeSession.DisposeAsync();

                    Reset(true);
'@

Set-Content -Path $managerPath -Value $text -NoNewline -Encoding utf8
Write-Host 'ShareX-Mod v0.4 manager hooks applied'
