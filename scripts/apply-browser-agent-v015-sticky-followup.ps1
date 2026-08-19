[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$text = [IO.File]::ReadAllText($worker)

$marker = "v0.1.5 hide-pinned-sticky"
if ($text.Contains($marker)) {
    Write-Host "[BrowserAgent-v0.1.5-followup] already: $marker" -ForegroundColor DarkYellow
    exit 0
}

$old = @'
    const touchesEdge = Math.abs(rect.top) <= 3 || Math.abs(rect.bottom - viewportHeight) <= 3 || Math.abs(rect.left) <= 3 || Math.abs(rect.right - viewportWidth) <= 3;
    if (style.position === "sticky" && !touchesEdge) continue;

    const visibleWidth = Math.max(0, Math.min(viewportWidth, rect.right) - Math.max(0, rect.left));
'@
$new = @'
    const touchesEdge = Math.abs(rect.top) <= 3 || Math.abs(rect.bottom - viewportHeight) <= 3 || Math.abs(rect.left) <= 3 || Math.abs(rect.right - viewportWidth) <= 3;
    // v0.1.5 hide-pinned-sticky: sticky controls can be pinned at a CSS inset
    // (for example an avatar 80px below the header) without touching an edge.
    if (style.position === "sticky") {
      const inset = name => {
        const raw = style.getPropertyValue(name);
        if (!raw || raw === "auto") return null;
        const value = Number.parseFloat(raw);
        return Number.isFinite(value) ? value : null;
      };
      const topInset = inset("top");
      const bottomInset = inset("bottom");
      const leftInset = inset("left");
      const rightInset = inset("right");
      const pinned =
        (topInset !== null && Math.abs(rect.top - topInset) <= 5) ||
        (bottomInset !== null && Math.abs(rect.bottom - (viewportHeight - bottomInset)) <= 5) ||
        (leftInset !== null && Math.abs(rect.left - leftInset) <= 5) ||
        (rightInset !== null && Math.abs(rect.right - (viewportWidth - rightInset)) <= 5);
      if (!pinned) continue;
    }

    const visibleWidth = Math.max(0, Math.min(viewportWidth, rect.right) - Math.max(0, rect.left));
'@

if (-not $text.Contains($old)) {
    throw "Browser Agent v0.1.5 hide-pinned-sticky anchor missing."
}
Write-Host "[BrowserAgent-v0.1.5-followup] compatible: $marker" -ForegroundColor Green
if (-not $CheckOnly) {
    [IO.File]::WriteAllText($worker, $text.Replace($old, $new), [Text.UTF8Encoding]::new($true))
    Write-Host "[BrowserAgent-v0.1.5-followup] applied: $marker" -ForegroundColor Cyan
}
