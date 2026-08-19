[CmdletBinding()]
param(
    [int]$MinimumScore = 95,
    [string]$OutputPath = "artifacts\browser-agent-v019-integrity-proof-score.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
Set-Location $repoRoot

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check {
    param([string]$Name, [int]$Points, [bool]$Passed, [bool]$Critical = $false, [string]$Detail = "")
    $checks.Add([pscustomobject]@{ name=$Name; points=$Points; passed=$Passed; critical=$Critical; detail=$Detail })
}
function Text([string]$Path) { [IO.File]::ReadAllText((Join-Path $repoRoot $Path)) }
function Has([string]$Text, [string]$Marker) { $Text.Contains($Marker) }

$proof = Text "LongCapture.Standalone\BrowserAgentIntegrityProofV019.cs"
$models = Text "LongCapture.Standalone\BrowserAgentModels.cs"
$session = Text "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$program = Text "LongCapture.Standalone\Program.cs"
$selftest = Text "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"
$project = Text "LongCapture.Standalone\LongCapture.Standalone.csproj"
$testing = Text "LongCapture.Standalone\TESTING.md"
$readme = Text "LongCapture.Standalone\BrowserAgent\README.md"
$worker = Text "LongCapture.Standalone\BrowserAgent\Extension\service-worker.js"
$manifest = Get-Content "LongCapture.Standalone\BrowserAgent\Extension\manifest.json" -Raw | ConvertFrom-Json

# A. Exact saved-byte integrity — 35.
Add-Check "new frame bytes stamped with SHA-256 before later replacement" 7 ((Has $proof 'StampNewCapture') -and (Has $models 'InitialFrameSha256') -and (Has $session 'BrowserAgentIntegrityProofV019.StampNewCapture(record, png)')) $true
Add-Check "recovery/repair replacement keeps revision provenance" 7 ((Has $proof 'StampRecapture') -and (Has $models 'PreviousAcceptedFrameSha256') -and (Has $models 'CaptureRevision') -and (Has $session 'StampRecapture(frame, png)')) $true
Add-Check "accepted frame files are re-read and compared before stitch" 7 ((Has $proof 'FrameHashMismatchCount') -and (Has $proof 'HashFile(framePath)') -and (Has $session 'FinalizePreStitch(sessionDirectory, manifest)')) $true
Add-Check "final PNG receives SHA-256 plus header verification" 7 ((Has $proof 'FinalImageSha256') -and (Has $proof 'FinalImagePngHeaderVerified') -and (Has $proof 'HasValidPngHeader(outputPath)')) $true
Add-Check "integrity.json itself receives a SHA-256 sidecar" 7 ((Has $proof 'ProofHashFileName = "integrity.json.sha256"') -and (Has $proof 'proofSha256') -and (Has $models 'IntegrityProofSha256')) $true

# B. Ordered Capture Map proof / coverage evidence — 30.
Add-Check "ordered RFC6962-style Merkle root with domain-separated leaf/node hashes" 8 ((Has $proof 'RFC6962-style') -and (Has $proof 'prefixed[0] = 0x00') -and (Has $proof 'payload[0] = 0x01') -and (Has $models 'IntegrityFrameLedgerRootSha256')) $true
Add-Check "logical sequence and coverage gaps are explicitly audited" 8 ((Has $proof 'SequenceGapCount') -and (Has $proof 'CoverageGapCount') -and (Has $proof 'previousCoverageEnd') -and (Has $models 'CoverageGapCss')) $true
Add-Check "exact identical frames at different logical Y are suspicious" 7 ((Has $proof 'ExactDuplicateDifferentPositionCount') -and (Has $proof 'ExactDuplicateOfSequence') -and (Has $session '[BA_HASH] frame=')) $true
Add-Check "proof-specific anomalies feed existing repair candidate ledger" 7 ((Has $session 'record.IntegrityProofSuspect') -and (Has $session 'record.IntegrityRiskScore += record.IntegrityProofRiskScore') -and (Has $session 'record.RepairCandidate = adaptiveDecision.RepairCandidate || integrity.Suspect || record.IntegrityProofSuspect')) $true

# C. Fail-closed and post-capture verification — 20.
Add-Check "critical frame-file mismatch blocks stitching" 5 ((Has $session 'failed-integrity-proof') -and (Has $session 'frame-file-integrity-failure') -and (Has $session 'stitching was blocked')) $true
Add-Check "coverage/duplicate uncertainty cannot become silent full verification" 5 ((Has $session 'integrity-proof-unresolved') -and (Has $proof 'quality-evidence-unresolved')) $true
Add-Check "offline verifier is quality-aware and cross-checks manifest/proof/final bytes" 5 ((Has $program '--verify-browser-agent-integrity=') -and (Has $proof 'the recorded capture proof is not fully verified') -and (Has $proof 'session.json quality/root state does not match integrity.json') -and (Has $proof 'session.json final-image evidence does not match integrity.json')) $true
Add-Check "deterministic self-test covers persisted verifier, tamper, unresolved quality and ordered root" 5 ((Has $proof 'VerifyExistingSession(root) != 0') -and (Has $proof 'VerifyExistingSession(root) == 0') -and (Has $proof 'offline verifier must reject unresolved-quality proof') -and (Has $proof 'tampered.FrameHashMismatchCount') -and (Has $selftest 'self-test failed v0.1.9 SHA-256/Merkle/coverage integrity proof')) $true

# D. Architecture, docs and security — 15.
$permissions = @($manifest.permissions)
$permissionBoundary = $permissions.Count -eq 3 -and
    $permissions -contains 'activeTab' -and
    $permissions -contains 'scripting' -and
    $permissions -contains 'nativeMessaging' -and
    $permissions -notcontains 'debugger' -and -not $manifest.host_permissions
Add-Check "extension permission boundary remains unchanged" 5 $permissionBoundary $true
$releaseCoherent = ([string]$manifest.name -eq 'LongCapture Browser Agent v0.1.9') -and
    ([string]$manifest.version -eq '0.1.9') -and
    (Has $worker 'protocolVersion: "0.1.9"') -and
    (Has $worker 'LongCapture Browser Agent v0.1.9 attached to this tab')
Add-Check "extension manifest, service-worker protocol and attached badge agree on v0.1.9" 0 $releaseCoherent $true "zero-point critical gate: release identity must never drift even when the numeric quality score is otherwise 100"
$noSha1Code = -not ($proof -match '\bSHA1\b|\bSHA-1\b')
Add-Check "new integrity code uses SHA-256, not deprecated SHA-1" 4 ($noSha1Code -and (Has $proof 'SHA256.HashData')) $true "NIST recommends SHA-2/SHA-3 for new collision-resistant uses"
$noHeavyHotPath = -not ($project -match '<PackageReference[^>]+(OpenCv|Tesseract|Paddle|OnnxRuntime)')
Add-Check "no heavy OCR/CV dependency added to per-frame hot path" 3 $noHeavyHotPath $false
$docsTruth = (Has $testing 'SHA-256') -and (Has $testing 'Merkle') -and (Has $testing 'hash does not prove') -and
    (Has $readme 'SHA-256') -and (Has $readme 'Coverage') -and (Has $readme 'does not prove')
Add-Check "release docs distinguish byte integrity from screenshot completeness" 3 $docsTruth $true

$maximum = ($checks | Measure-Object -Property points -Sum).Sum
$score = ($checks | Where-Object passed | Measure-Object -Property points -Sum).Sum
$criticalFailures = @($checks | Where-Object { $_.critical -and -not $_.passed })
$passed = $score -ge $MinimumScore -and $criticalFailures.Count -eq 0

$result = [pscustomobject]@{
    version = "0.1.9"
    gate = "sha256-merkle-capture-map-integrity-proof"
    score = $score
    maximum = $maximum
    minimum = $MinimumScore
    passed = $passed
    criticalFailures = $criticalFailures.Count
    evaluatedUtc = [DateTime]::UtcNow.ToString("O")
    checks = $checks
}

$directory = Split-Path -Parent $OutputPath
if ($directory) { New-Item -ItemType Directory -Force $directory | Out-Null }
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

Write-Host "============================================================" -ForegroundColor DarkGray
Write-Host "Browser Agent v0.1.9 PRE-BUILD INTEGRITY SCORE: $score / $maximum" -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
Write-Host "Required: >= $MinimumScore AND zero critical failures" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor DarkGray
foreach ($check in $checks) {
    $flag = if ($check.passed) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1} (+{2}) critical={3} {4}" -f $flag, $check.name, $check.points, $check.critical, $check.detail) -ForegroundColor $(if ($check.passed) { 'Green' } else { 'Yellow' })
}

if (-not $passed) {
    throw "Browser Agent v0.1.9 is not eligible to build: score $score/$maximum criticalFailures=$($criticalFailures.Count)."
}

Write-Host "v0.1.9 integrity proof is eligible for build/test. Real web-page pixels still require controlled user validation." -ForegroundColor Green
