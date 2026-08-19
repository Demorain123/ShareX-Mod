[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$HostName = 'com.longcapture.browser_agent'
$RegistryKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"
$ManifestPath = Join-Path (Join-Path $PSScriptRoot 'NativeHost') "$HostName.json"

if (Test-Path -LiteralPath $RegistryKey) {
    Remove-Item -LiteralPath $RegistryKey -Recurse -Force
}
if (Test-Path -LiteralPath $ManifestPath) {
    Remove-Item -LiteralPath $ManifestPath -Force
}

Write-Host 'LongCapture Browser Agent native host registration removed for the current Windows user.' -ForegroundColor Green
Write-Host 'The unpacked Chrome extension is not removed automatically; remove it from chrome://extensions if desired.'
