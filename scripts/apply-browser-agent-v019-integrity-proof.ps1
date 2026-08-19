[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

$project = Join-Path $repoRoot "LongCapture.Standalone\LongCapture.Standalone.csproj"
$models = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentModels.cs"
$session = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentCaptureSession.cs"
$program = Join-Path $repoRoot "LongCapture.Standalone\Program.cs"
$selftest = Join-Path $repoRoot "LongCapture.Standalone\BrowserAgentPocSelfTest.cs"

function Replace-One {
    param([string]$Path, [string]$Old, [string]$New, [string]$Marker)
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[BrowserAgent-v0.1.9-integrity] already: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Browser Agent v0.1.9 integrity compatibility anchor missing: $Marker in $Path"
    }
    Write-Host "[BrowserAgent-v0.1.9-integrity] compatible: $Marker" -ForegroundColor Green
    if (-not $CheckOnly) {
        [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($true))
        Write-Host "[BrowserAgent-v0.1.9-integrity] applied: $Marker" -ForegroundColor Cyan
    }
}

# Compile the staged helper only when the complete v0.1.9 model/session overlay is active.
Replace-One $project @'
    <Compile Remove="BrowserAgentIntegrityProofV019.cs" />
'@ @'
    <Compile Include="BrowserAgentIntegrityProofV019.cs" />
'@ '<Compile Include="BrowserAgentIntegrityProofV019.cs" />'

# Per-frame cryptographic provenance + coverage/duplicate evidence. SHA-256 is used;
# SHA-1 is deliberately not introduced for new integrity data.
Replace-One $models @'
    public List<BrowserAgentDomAnchorRecord> SemanticAnchors { get; set; } = new();

    // v0.1.2+:
'@ @'
    public List<BrowserAgentDomAnchorRecord> SemanticAnchors { get; set; } = new();

    // v0.1.9 capture-integrity proof. InitialFrameSha256 binds the first accepted
    // bytes; AcceptedFrameSha256 follows explicit recovery/repair replacements.
    public string InitialFrameSha256 { get; set; } = string.Empty;
    public string PreviousAcceptedFrameSha256 { get; set; } = string.Empty;
    public string AcceptedFrameSha256 { get; set; } = string.Empty;
    public long FrameByteLength { get; set; }
    public int CaptureRevision { get; set; }
    public bool IntegrityHashVerified { get; set; }
    public string AnchorEvidenceSha256 { get; set; } = string.Empty;
    public string LedgerLeafSha256 { get; set; } = string.Empty;
    public double CoverageGapCss { get; set; }
    public int ExactDuplicateOfSequence { get; set; }
    public bool IntegrityProofSuspect { get; set; }
    public int IntegrityProofRiskScore { get; set; }
    public string IntegrityProofRiskReasons { get; set; } = string.Empty;

    // v0.1.2+:
'@ 'public string InitialFrameSha256 { get; set; }'

Replace-One $models @'
    public int UnresolvedRepairFrames { get; set; }
    public bool RegionSelectionRequired { get; set; }
'@ @'
    public int UnresolvedRepairFrames { get; set; }

    // v0.1.9 post-capture integrity proof summary.
    public string IntegrityAlgorithm { get; set; } = "SHA-256";
    public string IntegrityFrameLedgerRootSha256 { get; set; } = string.Empty;
    public int IntegrityMissingFrameFiles { get; set; }
    public int IntegrityMissingExpectedHashes { get; set; }
    public int IntegrityFrameHashMismatches { get; set; }
    public int IntegritySequenceGaps { get; set; }
    public int IntegrityCoverageGaps { get; set; }
    public int IntegrityExactDuplicateFrames { get; set; }
    public string IntegrityProofStatus { get; set; } = "not-evaluated";
    public bool IntegrityProofVerified { get; set; }
    public string IntegrityProofFile { get; set; } = string.Empty;
    public string IntegrityProofSha256 { get; set; } = string.Empty;
    public string FinalImageSha256 { get; set; } = string.Empty;
    public long FinalImageByteLength { get; set; }
    public bool FinalImagePngHeaderVerified { get; set; }

    public bool RegionSelectionRequired { get; set; }
'@ 'public string IntegrityFrameLedgerRootSha256 { get; set; }'

Replace-One $models @'
    public string ProtocolVersion { get; set; } = "0.1.8";
'@ @'
    public string ProtocolVersion { get; set; } = "0.1.9";
'@ 'public string ProtocolVersion { get; set; } = "0.1.9";'

# Stamp the exact PNG bytes at capture time before any later repair can replace them.
Replace-One $session @'
        ApplyStability(record, response);
        ApplyEndConfirmation(record, response);
        return record;
'@ @'
        ApplyStability(record, response);
        ApplyEndConfirmation(record, response);
        BrowserAgentIntegrityProofV019.StampNewCapture(record, png);
        return record;
'@ 'BrowserAgentIntegrityProofV019.StampNewCapture(record, png);'

# Recovery and post-pass repair are legitimate byte replacements. Preserve the original
# digest, advance a revision counter and stamp the newly accepted exact bytes.
Replace-One $session @'
        frame.EndConfidence = string.Empty;
        ApplyStability(frame, response);
    }
'@ @'
        frame.EndConfidence = string.Empty;
        ApplyStability(frame, response);
        BrowserAgentIntegrityProofV019.StampRecapture(frame, png);
    }
'@ 'BrowserAgentIntegrityProofV019.StampRecapture(frame, png);'

# Cheap in-flight checks: logical coverage and exact-byte repeats at different positions.
Replace-One $session @'
                BrowserAgentFrameRecord record = await SaveNewFrameAsync(
                    sessionDirectory,
                    sequence,
                    response,
                    cancellationToken).ConfigureAwait(false);

                ValidateProgress(manifest.Frames, record);
'@ @'
                BrowserAgentFrameRecord record = await SaveNewFrameAsync(
                    sessionDirectory,
                    sequence,
                    response,
                    cancellationToken).ConfigureAwait(false);

                BrowserAgentIntegrityProofV019.ObserveFastPath(manifest.Frames, record);
                ValidateProgress(manifest.Frames, record);
'@ 'BrowserAgentIntegrityProofV019.ObserveFastPath(manifest.Frames, record);'

# Merge proof-specific suspicion into the existing multi-evidence v0.1.8 ledger.
Replace-One $session @'
                record.AnchorContinuityStatus = integrity.AnchorStatus;
                record.RepairCandidate = adaptiveDecision.RepairCandidate || integrity.Suspect ||
                    record.OverlapLeftEdgeContamination || record.OverlapRightEdgeContamination;
'@ @'
                record.AnchorContinuityStatus = integrity.AnchorStatus;
                if (record.IntegrityProofSuspect)
                {
                    record.IntegrityRiskScore += record.IntegrityProofRiskScore;
                    record.IntegrityRiskReasons = string.IsNullOrWhiteSpace(record.IntegrityRiskReasons)
                        ? record.IntegrityProofRiskReasons
                        : record.IntegrityRiskReasons + "," + record.IntegrityProofRiskReasons;
                    LongCaptureLog.Warn($"[BA_HASH] frame={record.Sequence} proofRisk={record.IntegrityProofRiskScore} reasons={LongCaptureLog.OneLine(record.IntegrityProofRiskReasons)} hash={record.AcceptedFrameSha256}");
                }
                record.RepairCandidate = adaptiveDecision.RepairCandidate || integrity.Suspect || record.IntegrityProofSuspect ||
                    record.OverlapLeftEdgeContamination || record.OverlapRightEdgeContamination;
'@ '[BA_HASH] frame='

# After all targeted repair, re-read every accepted frame from disk, verify its capture-time
# digest and check final logical coverage/order before the compositor is allowed to run.
Replace-One $session @'
        BrowserAgentAdaptiveTelemetryHub.Reset();

        status?.Invoke("Stitching verified frames using browser scroll geometry...");
'@ @'
        BrowserAgentAdaptiveTelemetryHub.Reset();

        status?.Invoke("Integrity proof: verifying accepted frame bytes, sequence and logical coverage...");
        BrowserAgentIntegrityProofSummaryV019 preStitchProof = BrowserAgentIntegrityProofV019.FinalizePreStitch(sessionDirectory, manifest);
        LongCaptureLog.Info($"[BA_HASH] pre-stitch status={preStitchProof.Status} root={preStitchProof.FrameLedgerMerkleRootSha256} " +
            $"critical={preStitchProof.CriticalFailures} gaps={preStitchProof.CoverageGapCount} duplicates={preStitchProof.ExactDuplicateDifferentPositionCount}");
        if (preStitchProof.CriticalFailures > 0)
        {
            manifest.Status = "failed-integrity-proof";
            manifest.StopReason = "frame-file-integrity-failure";
            manifest.CompletedUtc = DateTime.UtcNow;
            SaveManifest(manifestPath, manifest);
            throw new InvalidDataException($"Browser Agent integrity proof found {preStitchProof.CriticalFailures} critical frame-file failure(s); stitching was blocked.");
        }
        if (!preStitchProof.Passed)
        {
            stopReason = cancelled ? "manual-stop-integrity-unresolved" : "integrity-proof-unresolved";
            status?.Invoke($"Integrity proof found unresolved coverage/duplicate evidence: gaps={preStitchProof.CoverageGapCount}, exactRepeats={preStitchProof.ExactDuplicateDifferentPositionCount}. Result will remain Partial.");
        }
        SaveManifest(manifestPath, manifest);

        status?.Invoke("Stitching verified frames using browser scroll geometry...");
'@ 'BrowserAgentIntegrityProofSummaryV019 preStitchProof = BrowserAgentIntegrityProofV019.FinalizePreStitch'

# Hash and validate the final PNG, emit integrity.json + sidecar, then persist those digests
# into session.json. This proves saved-byte integrity; completeness still depends on the
# Capture Map / overlap / DOM / repair evidence above.
Replace-One $session @'
        manifest.FinalImage = Path.GetFileName(outputPath);
        manifest.CompletedUtc = DateTime.UtcNow;
        SaveManifest(manifestPath, manifest);
'@ @'
        manifest.FinalImage = Path.GetFileName(outputPath);
        BrowserAgentIntegrityProofSummaryV019 finalProof = BrowserAgentIntegrityProofV019.FinalizeOutput(sessionDirectory, manifest, outputPath);
        LongCaptureLog.Info($"[BA_HASH] final status={finalProof.Status} proof={manifest.IntegrityProofSha256} final={manifest.FinalImageSha256} root={manifest.IntegrityFrameLedgerRootSha256}");
        if (finalProof.CriticalFailures > 0 || !manifest.FinalImagePngHeaderVerified)
        {
            manifest.Status = "failed-integrity-proof";
            manifest.StopReason = "final-image-integrity-failure";
            manifest.CompletedUtc = DateTime.UtcNow;
            SaveManifest(manifestPath, manifest);
            throw new InvalidDataException("Browser Agent final PNG failed post-stitch integrity verification.");
        }
        manifest.CompletedUtc = DateTime.UtcNow;
        SaveManifest(manifestPath, manifest);
'@ 'BrowserAgentIntegrityProofSummaryV019 finalProof = BrowserAgentIntegrityProofV019.FinalizeOutput'

# Offline verification command for accidental corruption after the capture was saved.
Replace-One $program @'
        if (args.Any(x => string.Equals(x, "--browser-agent-poc-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = BrowserAgentPocSelfTest.Run();
            LongCaptureLog.Info($"--browser-agent-poc-self-test completed exitCode={code}");
            return code;
        }

        if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
'@ @'
        if (args.Any(x => string.Equals(x, "--browser-agent-poc-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = BrowserAgentPocSelfTest.Run();
            LongCaptureLog.Info($"--browser-agent-poc-self-test completed exitCode={code}");
            return code;
        }

        string? verifyIntegrityPath = args
            .FirstOrDefault(x => x.StartsWith("--verify-browser-agent-integrity=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--verify-browser-agent-integrity=".Length)
            .Trim('"');
        if (!string.IsNullOrWhiteSpace(verifyIntegrityPath))
        {
            int code = BrowserAgentIntegrityProofV019.VerifyExistingSession(verifyIntegrityPath, message => Console.WriteLine(message));
            LongCaptureLog.Info($"--verify-browser-agent-integrity completed exitCode={code} path={LongCaptureLog.OneLine(verifyIntegrityPath)}");
            return code;
        }

        if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
'@ '--verify-browser-agent-integrity='

# Browser deterministic self-test must exercise tamper, exact-repeat, gap and ordered-root cases.
Replace-One $selftest @'
            if (!BrowserAgentIntegrityPolicyV018.SelfTest() ||
                !BrowserAgentAdaptiveProfiles.UnlimitedRepairAttempts(BrowserAgentRepairPrecision.Perfect) ||
                BrowserAgentAdaptiveProfiles.DefaultRepairTimeLimitSeconds(BrowserAgentRepairPrecision.Perfect) < 600)
            {
                LongCaptureLog.Warn("Browser Agent self-test failed v0.1.8 integrity/Perfect repair policy");
                return 68;
            }

            if (!RunFrameCodecRoundTrip())
'@ @'
            if (!BrowserAgentIntegrityPolicyV018.SelfTest() ||
                !BrowserAgentAdaptiveProfiles.UnlimitedRepairAttempts(BrowserAgentRepairPrecision.Perfect) ||
                BrowserAgentAdaptiveProfiles.DefaultRepairTimeLimitSeconds(BrowserAgentRepairPrecision.Perfect) < 600)
            {
                LongCaptureLog.Warn("Browser Agent self-test failed v0.1.8 integrity/Perfect repair policy");
                return 68;
            }

            if (!BrowserAgentIntegrityProofV019.SelfTest())
            {
                LongCaptureLog.Warn("Browser Agent self-test failed v0.1.9 SHA-256/Merkle/coverage integrity proof");
                return 69;
            }

            if (!RunFrameCodecRoundTrip())
'@ 'self-test failed v0.1.9 SHA-256/Merkle/coverage integrity proof'

if ($CheckOnly) {
    Write-Host "Browser Agent v0.1.9 integrity-proof compatibility passed." -ForegroundColor Green
} else {
    Write-Host "Browser Agent v0.1.9 SHA-256 frame provenance, ordered Merkle ledger root, coverage proof and final-output verification applied." -ForegroundColor Green
}
