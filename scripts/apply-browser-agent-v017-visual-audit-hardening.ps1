[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$ui = Join-Path $repoRoot "LongCapture.Standalone\StandaloneUiPolish.cs"
$text = [IO.File]::ReadAllText($ui)
$marker = 'TryMeasureVisualAuditContent'

if ($text.Contains($marker)) {
    Write-Host "[BrowserAgent-v0.1.7-visual-audit] already present." -ForegroundColor DarkYellow
    exit 0
}

$oldStart = @'
                _ = form.Handle;
                form.WindowState = FormWindowState.Normal;
                form.MaximumSize = Size.Empty;
                foreach (Size size in new[]
'@
$newStart = @'
                _ = form.Handle;
                form.WindowState = FormWindowState.Normal;
                form.MaximumSize = Size.Empty;
                form.ShowInTaskbar = false; // v0.1.7 visual-audit shown-form rendering
                form.StartPosition = FormStartPosition.Manual;
                Rectangle auditWorkArea = Screen.FromControl(form).WorkingArea;
                form.Location = new Point(auditWorkArea.Left + 16, auditWorkArea.Top + 16);
                form.Show();
                Application.DoEvents();
                form.Refresh();
                Application.DoEvents();
                foreach (Size size in new[]
'@

$oldFrame = @'
                    form.Size = size;
                    form.PerformLayout();
                    Application.DoEvents();

                    int width = Math.Max(1, form.ClientSize.Width);
'@
$newFrame = @'
                    form.Size = size;
                    form.PerformLayout();
                    Application.DoEvents();
                    form.Refresh();
                    Application.DoEvents();

                    int width = Math.Max(1, form.ClientSize.Width);
'@

$oldBitmap = @'
                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
                    string name = $"LongCapture-v017-ui-{size.Width}x{size.Height}.png";
                    string path = Path.Combine(outputDirectory, name);
                    bitmap.Save(path, ImageFormat.Png);
                    captures.Add(new
                    {
                        requestedWindow = $"{size.Width}x{size.Height}",
                        actualClient = $"{width}x{height}",
                        file = name,
                        bytes = new FileInfo(path).Length
                    });
'@
$newBitmap = @'
                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
                    if (!TryMeasureVisualAuditContent(bitmap, out double interiorDarkFraction, out int interiorColors))
                    {
                        throw new InvalidOperationException(
                            $"visual-audit frame is visually blank or missing the real control hierarchy: requestedWindow={size.Width}x{size.Height} actualClient={width}x{height} interiorDarkFraction={interiorDarkFraction:F4} interiorColors={interiorColors}");
                    }
                    string name = $"LongCapture-v017-ui-{size.Width}x{size.Height}.png";
                    string path = Path.Combine(outputDirectory, name);
                    bitmap.Save(path, ImageFormat.Png);
                    captures.Add(new
                    {
                        requestedWindow = $"{size.Width}x{size.Height}",
                        actualClient = $"{width}x{height}",
                        interiorDarkFraction = Math.Round(interiorDarkFraction, 5),
                        interiorColors,
                        file = name,
                        bytes = new FileInfo(path).Length
                    });
'@

$oldFinally = @'
            finally
            {
                form.MaximumSize = originalMaximum;
                form.WindowState = originalState;
                form.Size = original;
                form.PerformLayout();
            }
'@
$newFinally = @'
            finally
            {
                if (form.Visible) form.Hide();
                form.MaximumSize = originalMaximum;
                form.WindowState = originalState;
                form.Size = original;
                form.PerformLayout();
            }
'@

$oldHelperAnchor = @'
    private static void StyleHeader(Label? title, Label? subtitle, bool highContrast)
'@
$newHelperAnchor = @'
    private static bool TryMeasureVisualAuditContent(Bitmap bitmap, out double darkFraction, out int interiorColors)
    {
        var colors = new HashSet<int>();
        long darkSamples = 0;
        long samples = 0;
        int left = Math.Min(8, Math.Max(0, bitmap.Width - 1));
        int top = Math.Min(72, Math.Max(0, bitmap.Height - 1));
        int right = Math.Max(left + 1, bitmap.Width - 8);
        int bottom = Math.Max(top + 1, bitmap.Height - 8);

        for (int y = top; y < bottom; y += 4)
        {
            for (int x = left; x < right; x += 4)
            {
                Color pixel = bitmap.GetPixel(x, y);
                colors.Add(pixel.ToArgb());
                double luminance = 0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B;
                if (luminance < 225.0) darkSamples++;
                samples++;
            }
        }

        interiorColors = colors.Count;
        darkFraction = samples == 0 ? 0 : darkSamples / (double)samples;
        return interiorColors >= 12 && darkFraction >= 0.002;
    }

    private static void StyleHeader(Label? title, Label? subtitle, bool highContrast)
'@

foreach ($pair in @(
    [pscustomobject]@{ Old = $oldStart; New = $newStart; Name = 'shown-form audit bootstrap' },
    [pscustomobject]@{ Old = $oldFrame; New = $newFrame; Name = 'refresh before bitmap capture' },
    [pscustomobject]@{ Old = $oldBitmap; New = $newBitmap; Name = 'semantic nonblank bitmap gate' },
    [pscustomobject]@{ Old = $oldFinally; New = $newFinally; Name = 'hide audit form during cleanup' },
    [pscustomobject]@{ Old = $oldHelperAnchor; New = $newHelperAnchor; Name = 'interior visual-content measurement helper' }
)) {
    if (-not $text.Contains($pair.Old)) {
        throw "Browser Agent v0.1.7 visual-audit hardening anchor missing: $($pair.Name)"
    }
    Write-Host "[BrowserAgent-v0.1.7-visual-audit] compatible: $($pair.Name)" -ForegroundColor Green
    $text = $text.Replace($pair.Old, $pair.New)
}

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.7 shown-form + semantic-content visual-audit compatibility passed." -ForegroundColor Green
    exit 0
}

[IO.File]::WriteAllText($ui, $text, [Text.UTF8Encoding]::new($true))
Write-Host "Browser Agent v0.1.7 visual audit hardened: shown WinForms hierarchy required and visually blank frames are rejected." -ForegroundColor Green
