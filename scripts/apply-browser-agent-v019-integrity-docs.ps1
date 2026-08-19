[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$testing = Join-Path $repoRoot "LongCapture.Standalone\TESTING.md"
$readme = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgent\README.md"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.9-docs] already: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "v0.1.9 docs compatibility anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
    }
    Write-Host "[BrowserAgent-v0.1.9-docs] compatible: $Marker" -ForegroundColor Green
}

Replace-One $testing @'
# LongCapture Browser Agent v0.1.8 — Integrity Repair Real Test Guide

'@ @'
# LongCapture Browser Agent v0.1.9 — Integrity Proof + Repair Real Test Guide

> v0.1.9 adds SHA-256 saved-byte provenance, an ordered Merkle Capture Map root, logical coverage/exact-repeat checks, `integrity.json`, and offline verification. **A hash does not prove screenshot completeness**: completeness still requires coordinate continuity, overlap/rail/DOM-anchor evidence, suspect-frame repair and final PNG validation.

## Integrity proof quick check

After any Browser Assisted capture finishes, open its session directory. A completed v0.1.9 session should contain `session.json`, `integrity.json`, `integrity.json.sha256`, the `frames` directory, and the final PNG.

Expected evidence:

- every accepted frame has `InitialFrameSha256` and `AcceptedFrameSha256`;
- a recovery/repair increments `CaptureRevision` while preserving the initial digest;
- `IntegrityFrameLedgerRootSha256` binds ordered frame bytes + logical Capture Map metadata using an RFC6962-style domain-separated Merkle root;
- `IntegrityCoverageGaps = 0`, `IntegrityFrameHashMismatches = 0`, and `IntegrityMissingFrameFiles = 0` for a fully verified result;
- the final PNG has `FinalImageSha256` and `FinalImagePngHeaderVerified = true`;
- `[BA_HASH]` log lines explain exact-repeat, pre-stitch and final proof decisions.

Offline re-check from PowerShell (using an exit code so it also works reliably with the GUI-subsystem executable):

```powershell
$Session = "<full path to BrowserAgent-YYYYMMDD-HHMMSS>"
$Arg = "--verify-browser-agent-integrity=`"$Session`""
$p = Start-Process -FilePath .\LongCapture.exe -ArgumentList $Arg -Wait -PassThru
$p.ExitCode
```

`0` means the persisted proof is fully verified **and** all frame/final-image bytes still match. A byte-clean session whose `integrity.json` still says coverage/duplicate/repair quality is unresolved must return non-zero; hash consistency is not allowed to override screenshot-quality evidence.

To prove the verifier is not ceremonial, copy a completed session to a temporary folder, change/delete one copied frame, then run the verifier on the copy: it must return non-zero. Do **not** alter the original evidence folder for this negative test.

'@ 'A hash does not prove screenshot completeness'

Replace-One $readme @'
# LongCapture Browser Agent v0.1.8 — Integrity Repair / Background Window / Requested End

'@ @'
# LongCapture Browser Agent v0.1.9 — Integrity Proof / Repair / Background Window / Requested End

## SHA-256 + Coverage Integrity Proof

v0.1.9 implements the baseline's three-layer model rather than treating a checksum as a screenshot-quality oracle:

1. **File integrity** — every accepted frame is stamped with SHA-256 at capture time, re-read before stitching, and repair/recovery replacements advance an explicit revision while retaining the original digest. The final PNG and `integrity.json` also receive SHA-256 evidence.
2. **Capture Integrity Ledger** — each final accepted frame contributes a canonical leaf containing its exact frame digest and critical Capture Map metadata. Ordered leaves are reduced to an RFC6962-style, domain-separated SHA-256 Merkle root stored as `IntegrityFrameLedgerRootSha256`.
3. **Coverage evidence** — sequence gaps, document-coordinate coverage gaps, exact identical frame bytes at different logical Y, overlap/rail/DOM-anchor/lazy/layout evidence and unresolved repair state remain independent quality signals.

A cryptographic hash proves that saved bytes match the bytes LongCapture accepted; it **does not prove** that the browser showed every intended document region. Therefore a clean SHA-256 alone can never upgrade a capture with unresolved coverage/repair evidence to “verified”. Critical hash/file mismatches fail closed before stitching. Coverage/exact-repeat anomalies feed the existing suspect-frame repair ledger and, if still unresolved after repair, keep the result Partial.

`integrity.json` contains the frame proofs, coverage summary, ordered Merkle root and final PNG digest. `integrity.json.sha256` protects the proof file against accidental later corruption. `LongCapture.exe --verify-browser-agent-integrity=<session>` rechecks the proof file, its fully-verified quality state, the manifest/root linkage, every frame and the final PNG. It returns success only when those layers agree. This is an accidental-corruption/integrity mechanism, not a cryptographic signature against an attacker who can rewrite the whole folder.

'@ '## SHA-256 + Coverage Integrity Proof'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.9 integrity release-doc compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.9 integrity proof documentation applied." -ForegroundColor Green
}
