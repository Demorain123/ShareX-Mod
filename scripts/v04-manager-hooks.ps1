$ErrorActionPreference = 'Stop'
$p = Join-Path $PSScriptRoot '..\ShareX.ScreenCaptureLib\ScrollingCaptureManager.cs'
$s = Get-Content $p -Raw

function R([string]$a,[string]$b) {
  if (-not $s.Contains($a)) { throw 'ShareX-Mod v0.4 anchor mismatch' }
  $script:s = $s.Replace($a,$b)
}

R '                ShareXModV04Settings v04Settings = ShareXModV04Settings.Load();' @'
                ShareXModV04Settings v04Settings = ShareXModV04Settings.Load();
                ShareXModSegmentStore segmentStore = robustSession != null ? ShareXModSegmentStore.TryCreate(v04Settings) : null;
                ShareXModChromeEnhancedSession chromeSession = null;
'@

R @'
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

R @'
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

R @'
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

Set-Content $p $s -NoNewline -Encoding utf8
Write-Host 'v0.4 manager hooks ready'
