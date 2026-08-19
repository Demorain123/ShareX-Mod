[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$project = Join-Path $repoRoot "LongCapture.Standalone\LongCapture.Standalone.csproj"
$text = [IO.File]::ReadAllText($project)
$remove = '    <Compile Remove="BrowserAgentIntegrityProofV019.cs" />'
$marker = '<Compile Include="BrowserAgentIntegrityProofV019.cs" />'
$replacement = '    <!-- v0.1.9 proof source enabled by SDK default Compile items; compatibility marker only: <Compile Include="BrowserAgentIntegrityProofV019.cs" /> -->'

if ($text.Contains($replacement)) {
    Write-Host "[BrowserAgent-v0.1.9-project-compat] SDK default Compile inclusion already enabled." -ForegroundColor DarkYellow
    exit 0
}
if (-not $text.Contains($remove)) {
    throw "Browser Agent v0.1.9 project compatibility anchor missing: staged Compile Remove item"
}
if (-not $CheckOnly) {
    [IO.File]::WriteAllText($project, $text.Replace($remove, $replacement), [Text.UTF8Encoding]::new($true))
    Write-Host "[BrowserAgent-v0.1.9-project-compat] removed staged exclusion; SDK default Compile now owns BrowserAgentIntegrityProofV019.cs." -ForegroundColor Cyan
} else {
    Write-Host "[BrowserAgent-v0.1.9-project-compat] staged project is compatible with SDK default Compile enablement." -ForegroundColor Green
}
