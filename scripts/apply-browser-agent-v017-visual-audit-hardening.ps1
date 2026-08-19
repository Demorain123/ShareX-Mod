[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$ui = Join-Path $repoRoot "LongCapture.Standalone\StandaloneUiPolish.cs"
$text = [IO.File]::ReadAllText($ui)
$marker = 'form.ShowInTaskbar = false; // v0.1.7 visual-audit shown-form rendering'

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

foreach ($pair in @(
    [pscustomobject]@{ Old = $oldStart; New = $newStart; Name = 'shown-form audit bootstrap' },
    [pscustomobject]@{ Old = $oldFrame; New = $newFrame; Name = 'refresh before bitmap capture' },
    [pscustomobject]@{ Old = $oldFinally; New = $newFinally; Name = 'hide audit form during cleanup' }
)) {
    if (-not $text.Contains($pair.Old)) {
        throw "Browser Agent v0.1.7 visual-audit hardening anchor missing: $($pair.Name)"
    }
    Write-Host "[BrowserAgent-v0.1.7-visual-audit] compatible: $($pair.Name)" -ForegroundColor Green
    $text = $text.Replace($pair.Old, $pair.New)
}

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.7 shown-form visual-audit compatibility passed." -ForegroundColor Green
    exit 0
}

[IO.File]::WriteAllText($ui, $text, [Text.UTF8Encoding]::new($true))
Write-Host "Browser Agent v0.1.7 visual audit hardened: the real shown WinForms hierarchy is now rendered before screenshot capture." -ForegroundColor Green
