[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$ExtensionId
)

$ErrorActionPreference = 'Stop'
$HostName = 'com.longcapture.browser_agent'
$BrowserAgentDir = $PSScriptRoot
$LongCaptureDir = Split-Path -Parent $BrowserAgentDir
$ExePath = Join-Path $LongCaptureDir 'LongCapture.exe'
$NativeHostDir = Join-Path $BrowserAgentDir 'NativeHost'
$ManifestPath = Join-Path $NativeHostDir "$HostName.json"
$RegistryKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "LongCapture.exe was not found at: $ExePath"
}

if ([string]::IsNullOrWhiteSpace($ExtensionId)) {
    Write-Host ''
    Write-Host 'Open chrome://extensions, enable Developer mode, load the unpacked BrowserAgent\Extension folder,' -ForegroundColor Cyan
    Write-Host 'then copy the 32-character extension ID shown by Chrome.' -ForegroundColor Cyan
    Write-Host ''
    $ExtensionId = Read-Host 'Chrome extension ID'
}

$ExtensionId = $ExtensionId.Trim().ToLowerInvariant()
if ($ExtensionId -notmatch '^[a-p]{32}$') {
    throw "Invalid Chrome extension ID '$ExtensionId'. Expected exactly 32 characters using a-p."
}

New-Item -ItemType Directory -Path $NativeHostDir -Force | Out-Null
$manifest = [ordered]@{
    name = $HostName
    description = 'LongCapture Browser Agent v0.1 PoC native messaging bridge'
    path = $ExePath
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
New-Item -Path $RegistryKey -Force | Out-Null
Set-Item -Path $RegistryKey -Value $ManifestPath

Write-Host ''
Write-Host 'LongCapture Browser Agent native host installed for the current Windows user.' -ForegroundColor Green
Write-Host "Extension ID : $ExtensionId"
Write-Host "Host manifest: $ManifestPath"
Write-Host "LongCapture   : $ExePath"
Write-Host ''
Write-Host 'Next: start START-BROWSER-AGENT-POC.cmd, activate the target Chrome tab, and click the extension icon.' -ForegroundColor Yellow
