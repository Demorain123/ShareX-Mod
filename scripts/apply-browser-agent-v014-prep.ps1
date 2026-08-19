[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$worker = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$text = [IO.File]::ReadAllText($worker)

$old = '    const finish = value => {'
$new = '    const finish = (value) => {'
if ($text.Contains($new)) {
    Write-Host "[BrowserAgent-v0.1.4-prep] picker finish anchor already normalized." -ForegroundColor DarkYellow
    exit 0
}
if (-not $text.Contains($old)) {
    throw "Browser Agent v0.1.4 prep could not find the v0.1.3 picker finish anchor."
}
Write-Host "[BrowserAgent-v0.1.4-prep] picker finish anchor compatible." -ForegroundColor Green
if (-not $CheckOnly) {
    [IO.File]::WriteAllText($worker, $text.Replace($old, $new), [Text.UTF8Encoding]::new($true))
    Write-Host "[BrowserAgent-v0.1.4-prep] picker finish anchor normalized." -ForegroundColor Cyan
}
