[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "apply-v04-hooks.ps1"
$temp = Join-Path $env:TEMP ("sharex-mod-v04-hooks-" + [Guid]::NewGuid().ToString("N") + ".ps1")

try {
    $text = [IO.File]::ReadAllText($source)
    $bad = '-Marker "string modEndReason = stopRequested ? \"manual-stop\" : \"capture-ended\";"'
    $good = '-Marker "modEndReason"'
    if ($text.Contains($bad)) {
        $text = $text.Replace($bad, $good)
    }
    [IO.File]::WriteAllText($temp, $text, [Text.UTF8Encoding]::new($false))
    if ($CheckOnly) { & pwsh -NoProfile -File $temp -CheckOnly }
    else { & pwsh -NoProfile -File $temp }
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}
