[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$program = Join-Path $repoRoot "LongCapture.Standalone\Program.cs"
$text = [IO.File]::ReadAllText($program)
$marker = '--modern-ui-v017-audit'

if ($text.Contains($marker)) {
    Write-Host "[BrowserAgent-v0.1.7-ui] audit CLI already present." -ForegroundColor DarkYellow
} else {
    $old = @'
        using var exclusionWatcher = CaptureExclusionWatcher.Start();
'@
    $new = @'
        string? modernUiAuditArg = args.FirstOrDefault(x =>
            x.StartsWith("--modern-ui-v017-audit", StringComparison.OrdinalIgnoreCase));
        if (modernUiAuditArg is not null)
        {
            string output = modernUiAuditArg.Contains('=')
                ? modernUiAuditArg[(modernUiAuditArg.IndexOf('=') + 1)..].Trim('"')
                : Path.Combine(AppContext.BaseDirectory, "UiAudit-v017");
            using var auditForm = new MainForm();
            StandaloneUiPolish.Apply(auditForm);
            int code = StandaloneUiPolish.CaptureVisualAudit(auditForm, output, out string detail);
            Console.WriteLine($"LongCapture v0.1.7 UI audit: {detail}");
            LongCaptureLog.Info($"--modern-ui-v017-audit completed exitCode={code} detail={LongCaptureLog.OneLine(detail)} output={LongCaptureLog.OneLine(output)}");
            return code;
        }

        using var exclusionWatcher = CaptureExclusionWatcher.Start();
'@

    if (-not $text.Contains($old)) {
        throw "Browser Agent v0.1.7 modern-UI audit anchor missing. Apply v0.1 Browser overlay first."
    }
    Write-Host "[BrowserAgent-v0.1.7-ui] compatible: modern UI visual-audit CLI." -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($program, $text.Replace($old, $new), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.7-ui] audit CLI applied." -ForegroundColor Cyan
    }
}

$followups = @(
    (Join-Path $PSScriptRoot "apply-browser-agent-v017-responsive-followup.ps1"),
    (Join-Path $PSScriptRoot "apply-browser-agent-v017-visual-audit-hardening.ps1")
)
foreach ($followup in $followups) {
    if (-not (Test-Path -LiteralPath $followup)) {
        throw "Missing Browser Agent v0.1.7 UI follow-up: $followup"
    }
    if ($CheckOnly) {
        & pwsh -NoProfile -File $followup -CheckOnly
    } else {
        & pwsh -NoProfile -File $followup
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.7 modern responsive UI compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.7 modern responsive UI overlay chain applied." -ForegroundColor Green
}
