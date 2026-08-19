[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }
$proofPath = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentIntegrityProofV019.cs"
$text = [IO.File]::ReadAllText($proofPath)

$qualityMarker = 'Integrity verification failed: the recorded capture proof is not fully verified'
if (-not $text.Contains($qualityMarker)) {
    $old = @'
            if (!string.IsNullOrWhiteSpace(manifest.IntegrityProofSha256) &&
                !string.Equals(manifest.IntegrityProofSha256, actualProofHash, StringComparison.OrdinalIgnoreCase))
            {
                report?.Invoke("Integrity verification failed: session.json does not point to the current integrity.json digest.");
                return 5;
            }

            foreach (BrowserAgentIntegrityFrameProofV019 frame in proof.Frames)
'@
    $new = @'
            if (!string.IsNullOrWhiteSpace(manifest.IntegrityProofSha256) &&
                !string.Equals(manifest.IntegrityProofSha256, actualProofHash, StringComparison.OrdinalIgnoreCase))
            {
                report?.Invoke("Integrity verification failed: session.json does not point to the current integrity.json digest.");
                return 5;
            }

            // Offline verification is a quality+integrity verifier, not only a byte re-hasher.
            // A proof with unresolved coverage/duplicate/repair evidence must never print PASS.
            if (!proof.Passed || proof.CriticalFailures != 0 ||
                !string.Equals(proof.Status, "verified", StringComparison.OrdinalIgnoreCase))
            {
                report?.Invoke($"Integrity verification failed: the recorded capture proof is not fully verified (status={proof.Status}, critical={proof.CriticalFailures}, gaps={proof.CoverageGapCount}, duplicates={proof.ExactDuplicateDifferentPositionCount}, unresolvedRepair={proof.UnresolvedRepairFrames}).");
                return 8;
            }
            if (!manifest.IntegrityProofVerified ||
                !string.Equals(manifest.IntegrityProofStatus, proof.Status, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.IntegrityFrameLedgerRootSha256, proof.FrameLedgerMerkleRootSha256, StringComparison.OrdinalIgnoreCase))
            {
                report?.Invoke("Integrity verification failed: session.json quality/root state does not match integrity.json.");
                return 9;
            }
            if (!string.Equals(manifest.FinalImageSha256, proof.FinalImageSha256, StringComparison.OrdinalIgnoreCase) ||
                manifest.FinalImageByteLength != proof.FinalImageByteLength ||
                manifest.FinalImagePngHeaderVerified != proof.FinalImagePngHeaderVerified)
            {
                report?.Invoke("Integrity verification failed: session.json final-image evidence does not match integrity.json.");
                return 10;
            }

            foreach (BrowserAgentIntegrityFrameProofV019 frame in proof.Frames)
'@
    if (-not $text.Contains($old)) { throw "v0.1.9 verifier-hardening anchor missing: manifest proof linkage" }
    if (-not $CheckOnly) {
        $text = $text.Replace($old, $new)
    }
}

$selfTestMarker = 'offline verifier must reject unresolved-quality proof'
if (-not $text.Contains($selfTestMarker)) {
    $old = @'
            BrowserAgentIntegrityProofSummaryV019 cleanProof = FinalizePreStitch(root, clean);
            if (!cleanProof.Passed || cleanProof.CriticalFailures != 0 || cleanProof.CoverageGapCount != 0) return false;
            string cleanRoot = cleanProof.FrameLedgerMerkleRootSha256;

            // Same bytes at a different logical position are suspicious and a real gap is unresolved.
'@
    $new = @'
            BrowserAgentIntegrityProofSummaryV019 cleanProof = FinalizePreStitch(root, clean);
            if (!cleanProof.Passed || cleanProof.CriticalFailures != 0 || cleanProof.CoverageGapCount != 0) return false;
            string cleanRoot = cleanProof.FrameLedgerMerkleRootSha256;

            // Exercise the complete persisted proof + offline verifier, not only helper functions.
            byte[] onePixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            string finalPath = Path.Combine(root, "final.png");
            File.WriteAllBytes(finalPath, onePixelPng);
            BrowserAgentIntegrityProofSummaryV019 persistedClean = FinalizeOutput(root, clean, finalPath);
            File.WriteAllText(Path.Combine(root, "session.json"), JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true }));
            if (!persistedClean.Passed || VerifyExistingSession(root) != 0) return false;

            // Accidental frame-byte mutation after proof generation must be rejected.
            File.WriteAllBytes(Path.Combine(root, "frames", "frame-0001.png"), Encoding.UTF8.GetBytes("changed-after-proof"));
            if (VerifyExistingSession(root) == 0) return false;
            File.WriteAllBytes(Path.Combine(root, "frames", "frame-0001.png"), a);

            // Same bytes at a different logical position are suspicious and a real gap is unresolved.
'@
    if (-not $text.Contains($old)) { throw "v0.1.9 verifier-hardening anchor missing: clean self-test" }
    if (-not $CheckOnly) {
        $text = $text.Replace($old, $new)
    }

    $old2 = @'
            BrowserAgentIntegrityProofSummaryV019 suspicious = FinalizePreStitch(root, clean);
            if (suspicious.Passed || suspicious.ExactDuplicateDifferentPositionCount < 1 || suspicious.CoverageGapCount < 1) return false;
            if (string.Equals(cleanRoot, suspicious.FrameLedgerMerkleRootSha256, StringComparison.OrdinalIgnoreCase)) return false;

            // Mutating a frame after its accepted digest was stamped must be a critical mismatch.
'@
    $new2 = @'
            BrowserAgentIntegrityProofSummaryV019 suspicious = FinalizePreStitch(root, clean);
            if (suspicious.Passed || suspicious.ExactDuplicateDifferentPositionCount < 1 || suspicious.CoverageGapCount < 1) return false;
            if (string.Equals(cleanRoot, suspicious.FrameLedgerMerkleRootSha256, StringComparison.OrdinalIgnoreCase)) return false;

            // The offline verifier must reject unresolved-quality proof even if all saved bytes match.
            // offline verifier must reject unresolved-quality proof
            BrowserAgentIntegrityProofSummaryV019 persistedSuspicious = FinalizeOutput(root, clean, finalPath);
            File.WriteAllText(Path.Combine(root, "session.json"), JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true }));
            if (persistedSuspicious.Passed || VerifyExistingSession(root) == 0) return false;

            // Mutating a frame after its accepted digest was stamped must be a critical mismatch.
'@
    if (-not $text.Contains($old2)) { throw "v0.1.9 verifier-hardening anchor missing: suspicious self-test" }
    if (-not $CheckOnly) {
        $text = $text.Replace($old2, $new2)
    }
}

if (-not $CheckOnly) {
    [IO.File]::WriteAllText($proofPath, $text, [Text.UTF8Encoding]::new($true))
    Write-Host "Browser Agent v0.1.9 offline verifier hardened: unresolved quality can no longer report PASS; persisted/tamper paths self-tested." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.9 verifier-hardening compatibility passed." -ForegroundColor Green
}
