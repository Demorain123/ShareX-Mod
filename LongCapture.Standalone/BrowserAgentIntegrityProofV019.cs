using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentIntegrityFrameProofV019
{
    public int Sequence { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FrameSha256 { get; set; } = string.Empty;
    public long FrameByteLength { get; set; }
    public int CaptureRevision { get; set; }
    public double ScrollYCss { get; set; }
    public double ViewportHeightCss { get; set; }
    public double CoverageGapCss { get; set; }
    public int ExactDuplicateOfSequence { get; set; }
    public string AnchorEvidenceSha256 { get; set; } = string.Empty;
    public string LedgerLeafSha256 { get; set; } = string.Empty;
    public bool FileHashVerified { get; set; }
    public string QualityState { get; set; } = string.Empty;
}

internal sealed class BrowserAgentIntegrityProofSummaryV019
{
    public string Version { get; set; } = "0.1.9";
    public string Algorithm { get; set; } = "SHA-256";
    public string TreeConstruction { get; set; } = "RFC6962-style domain-separated Merkle root";
    public DateTime EvaluatedUtc { get; set; }
    public string Status { get; set; } = "unknown";
    public bool Passed { get; set; }
    public int CriticalFailures { get; set; }
    public int MissingFrameFileCount { get; set; }
    public int MissingExpectedHashCount { get; set; }
    public int FrameHashMismatchCount { get; set; }
    public int SequenceGapCount { get; set; }
    public int CoverageGapCount { get; set; }
    public double TotalCoverageGapCss { get; set; }
    public int ExactDuplicateDifferentPositionCount { get; set; }
    public int UnresolvedRepairFrames { get; set; }
    public string FrameLedgerMerkleRootSha256 { get; set; } = string.Empty;
    public string FinalImageFileName { get; set; } = string.Empty;
    public string FinalImageSha256 { get; set; } = string.Empty;
    public long FinalImageByteLength { get; set; }
    public bool FinalImagePngHeaderVerified { get; set; }
    public List<BrowserAgentIntegrityFrameProofV019> Frames { get; set; } = new();
}

internal static class BrowserAgentIntegrityProofV019
{
    public const string AlgorithmName = "SHA-256";
    public const string ProofFileName = "integrity.json";
    public const string ProofHashFileName = "integrity.json.sha256";
    private const double CoverageToleranceCss = 3.0;

    public static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void StampNewCapture(BrowserAgentFrameRecord frame, ReadOnlySpan<byte> png)
    {
        string hash = HashBytes(png);
        frame.InitialFrameSha256 = hash;
        frame.AcceptedFrameSha256 = hash;
        frame.PreviousAcceptedFrameSha256 = string.Empty;
        frame.FrameByteLength = png.Length;
        frame.CaptureRevision = 0;
        frame.IntegrityHashVerified = true;
    }

    public static void StampRecapture(BrowserAgentFrameRecord frame, ReadOnlySpan<byte> png)
    {
        string hash = HashBytes(png);
        if (string.IsNullOrWhiteSpace(frame.InitialFrameSha256)) frame.InitialFrameSha256 = hash;
        frame.PreviousAcceptedFrameSha256 = frame.AcceptedFrameSha256;
        frame.AcceptedFrameSha256 = hash;
        frame.FrameByteLength = png.Length;
        frame.CaptureRevision++;
        frame.IntegrityHashVerified = true;
        frame.LedgerLeafSha256 = string.Empty;
    }

    // Cheap first-pass evidence only: exact byte identity and logical coverage.
    // Cryptographic hashes are not used as a visual-similarity score. They merely
    // surface impossible/suspicious exact repeats and bind captured bytes to the ledger.
    public static void ObserveFastPath(IReadOnlyList<BrowserAgentFrameRecord> previousFrames, BrowserAgentFrameRecord current)
    {
        current.IntegrityProofSuspect = false;
        current.IntegrityProofRiskScore = 0;
        current.IntegrityProofRiskReasons = string.Empty;
        current.CoverageGapCss = 0;
        current.ExactDuplicateOfSequence = 0;

        if (previousFrames.Count > 0)
        {
            BrowserAgentFrameRecord previous = previousFrames[^1];
            double previousCoverageEnd = previous.ScrollYCss + Math.Max(0, previous.ViewportHeightCss);
            double gap = current.ScrollYCss - previousCoverageEnd;
            if (gap > CoverageToleranceCss)
            {
                current.CoverageGapCss = gap;
                AddFastRisk(current, 10, $"coverage-gap:{gap:F1}px");
            }
        }

        if (!string.IsNullOrWhiteSpace(current.AcceptedFrameSha256))
        {
            BrowserAgentFrameRecord? duplicate = previousFrames.FirstOrDefault(frame =>
                !string.IsNullOrWhiteSpace(frame.AcceptedFrameSha256) &&
                string.Equals(frame.AcceptedFrameSha256, current.AcceptedFrameSha256, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(frame.ScrollYCss - current.ScrollYCss) > 1.0);
            if (duplicate is not null)
            {
                current.ExactDuplicateOfSequence = duplicate.Sequence;
                AddFastRisk(current, 8, $"exact-frame-repeat-of:{duplicate.Sequence}");
            }
        }
    }

    public static BrowserAgentIntegrityProofSummaryV019 FinalizePreStitch(
        string sessionDirectory,
        BrowserAgentSessionManifest manifest)
    {
        var summary = new BrowserAgentIntegrityProofSummaryV019
        {
            EvaluatedUtc = DateTime.UtcNow,
            UnresolvedRepairFrames = manifest.UnresolvedRepairFrames
        };

        var duplicateByHash = new Dictionary<string, BrowserAgentFrameRecord>(StringComparer.OrdinalIgnoreCase);
        var leafHashes = new List<byte[]>();
        BrowserAgentFrameRecord? previous = null;

        foreach (BrowserAgentFrameRecord frame in manifest.Frames)
        {
            frame.IntegrityHashVerified = false;
            frame.CoverageGapCss = 0;
            frame.ExactDuplicateOfSequence = 0;

            string framePath = Path.Combine(sessionDirectory, frame.FileName);
            string actualHash = string.Empty;
            long byteLength = 0;
            bool fileHashVerified = false;

            if (!File.Exists(framePath))
            {
                summary.MissingFrameFileCount++;
            }
            else
            {
                var info = new FileInfo(framePath);
                byteLength = info.Length;
                actualHash = HashFile(framePath);
                if (string.IsNullOrWhiteSpace(frame.AcceptedFrameSha256))
                {
                    summary.MissingExpectedHashCount++;
                }
                else if (!string.Equals(actualHash, frame.AcceptedFrameSha256, StringComparison.OrdinalIgnoreCase))
                {
                    summary.FrameHashMismatchCount++;
                }
                else
                {
                    fileHashVerified = true;
                }
            }

            frame.IntegrityHashVerified = fileHashVerified;
            frame.FrameByteLength = byteLength;

            if (previous is not null)
            {
                if (frame.Sequence != previous.Sequence + 1) summary.SequenceGapCount++;
                double previousCoverageEnd = previous.ScrollYCss + Math.Max(0, previous.ViewportHeightCss);
                double gap = frame.ScrollYCss - previousCoverageEnd;
                if (gap > CoverageToleranceCss)
                {
                    frame.CoverageGapCss = gap;
                    summary.CoverageGapCount++;
                    summary.TotalCoverageGapCss += gap;
                }
            }

            if (!string.IsNullOrWhiteSpace(actualHash))
            {
                if (duplicateByHash.TryGetValue(actualHash, out BrowserAgentFrameRecord? first) &&
                    Math.Abs(first.ScrollYCss - frame.ScrollYCss) > 1.0)
                {
                    frame.ExactDuplicateOfSequence = first.Sequence;
                    summary.ExactDuplicateDifferentPositionCount++;
                }
                else if (!duplicateByHash.ContainsKey(actualHash))
                {
                    duplicateByHash.Add(actualHash, frame);
                }
            }

            frame.AnchorEvidenceSha256 = ComputeAnchorEvidenceHash(frame.SemanticAnchors);
            string canonical = CanonicalFrameLedgerEntry(frame, actualHash, byteLength);
            byte[] leafHash = HashLeaf(Encoding.UTF8.GetBytes(canonical));
            frame.LedgerLeafSha256 = Convert.ToHexString(leafHash).ToLowerInvariant();
            leafHashes.Add(leafHash);

            summary.Frames.Add(new BrowserAgentIntegrityFrameProofV019
            {
                Sequence = frame.Sequence,
                FileName = frame.FileName,
                FrameSha256 = actualHash,
                FrameByteLength = byteLength,
                CaptureRevision = frame.CaptureRevision,
                ScrollYCss = frame.ScrollYCss,
                ViewportHeightCss = frame.ViewportHeightCss,
                CoverageGapCss = frame.CoverageGapCss,
                ExactDuplicateOfSequence = frame.ExactDuplicateOfSequence,
                AnchorEvidenceSha256 = frame.AnchorEvidenceSha256,
                LedgerLeafSha256 = frame.LedgerLeafSha256,
                FileHashVerified = fileHashVerified,
                QualityState = frame.QualityState
            });

            previous = frame;
        }

        summary.FrameLedgerMerkleRootSha256 = Convert.ToHexString(ComputeMerkleRoot(leafHashes)).ToLowerInvariant();
        summary.CriticalFailures = summary.MissingFrameFileCount + summary.MissingExpectedHashCount + summary.FrameHashMismatchCount;
        int qualityWarnings = summary.SequenceGapCount + summary.CoverageGapCount +
            summary.ExactDuplicateDifferentPositionCount + summary.UnresolvedRepairFrames;
        summary.Passed = summary.CriticalFailures == 0 && qualityWarnings == 0;
        summary.Status = summary.CriticalFailures > 0
            ? "critical-file-integrity-failure"
            : qualityWarnings > 0
                ? "quality-evidence-unresolved"
                : "verified";

        manifest.IntegrityAlgorithm = AlgorithmName;
        manifest.IntegrityFrameLedgerRootSha256 = summary.FrameLedgerMerkleRootSha256;
        manifest.IntegrityMissingFrameFiles = summary.MissingFrameFileCount;
        manifest.IntegrityMissingExpectedHashes = summary.MissingExpectedHashCount;
        manifest.IntegrityFrameHashMismatches = summary.FrameHashMismatchCount;
        manifest.IntegritySequenceGaps = summary.SequenceGapCount;
        manifest.IntegrityCoverageGaps = summary.CoverageGapCount;
        manifest.IntegrityExactDuplicateFrames = summary.ExactDuplicateDifferentPositionCount;
        manifest.IntegrityProofStatus = summary.Status;
        manifest.IntegrityProofVerified = summary.Passed;
        return summary;
    }

    public static BrowserAgentIntegrityProofSummaryV019 FinalizeOutput(
        string sessionDirectory,
        BrowserAgentSessionManifest manifest,
        string outputPath)
    {
        BrowserAgentIntegrityProofSummaryV019 summary = FinalizePreStitch(sessionDirectory, manifest);
        if (File.Exists(outputPath))
        {
            summary.FinalImageFileName = Path.GetFileName(outputPath);
            summary.FinalImageByteLength = new FileInfo(outputPath).Length;
            summary.FinalImageSha256 = HashFile(outputPath);
            summary.FinalImagePngHeaderVerified = HasValidPngHeader(outputPath);
        }

        if (!summary.FinalImagePngHeaderVerified || string.IsNullOrWhiteSpace(summary.FinalImageSha256))
        {
            summary.CriticalFailures++;
            summary.Passed = false;
            summary.Status = "critical-final-image-integrity-failure";
        }

        string proofPath = Path.Combine(sessionDirectory, ProofFileName);
        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(proofPath + ".tmp", json, new UTF8Encoding(false));
        File.Move(proofPath + ".tmp", proofPath, overwrite: true);
        string proofSha256 = HashFile(proofPath);
        File.WriteAllText(
            Path.Combine(sessionDirectory, ProofHashFileName),
            $"{proofSha256}  {ProofFileName}{Environment.NewLine}",
            new UTF8Encoding(false));

        manifest.IntegrityProofFile = ProofFileName;
        manifest.IntegrityProofSha256 = proofSha256;
        manifest.FinalImageSha256 = summary.FinalImageSha256;
        manifest.FinalImageByteLength = summary.FinalImageByteLength;
        manifest.FinalImagePngHeaderVerified = summary.FinalImagePngHeaderVerified;
        manifest.IntegrityProofStatus = summary.Status;
        manifest.IntegrityProofVerified = summary.Passed;
        return summary;
    }

    public static int VerifyExistingSession(string sessionDirectory, Action<string>? report = null)
    {
        try
        {
            string proofPath = Path.Combine(sessionDirectory, ProofFileName);
            string proofHashPath = Path.Combine(sessionDirectory, ProofHashFileName);
            string manifestPath = Path.Combine(sessionDirectory, "session.json");
            if (!File.Exists(proofPath) || !File.Exists(proofHashPath) || !File.Exists(manifestPath))
            {
                report?.Invoke("Integrity verification failed: session.json / integrity.json / integrity.json.sha256 is missing.");
                return 2;
            }

            string expectedProofHash = File.ReadAllText(proofHashPath).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            string actualProofHash = HashFile(proofPath);
            if (!string.Equals(expectedProofHash, actualProofHash, StringComparison.OrdinalIgnoreCase))
            {
                report?.Invoke("Integrity verification failed: integrity.json SHA-256 does not match its sidecar.");
                return 3;
            }

            BrowserAgentIntegrityProofSummaryV019? proof = JsonSerializer.Deserialize<BrowserAgentIntegrityProofSummaryV019>(File.ReadAllText(proofPath));
            BrowserAgentSessionManifest? manifest = JsonSerializer.Deserialize<BrowserAgentSessionManifest>(File.ReadAllText(manifestPath));
            if (proof is null || manifest is null)
            {
                report?.Invoke("Integrity verification failed: proof or manifest could not be parsed.");
                return 4;
            }
            if (!string.IsNullOrWhiteSpace(manifest.IntegrityProofSha256) &&
                !string.Equals(manifest.IntegrityProofSha256, actualProofHash, StringComparison.OrdinalIgnoreCase))
            {
                report?.Invoke("Integrity verification failed: session.json does not point to the current integrity.json digest.");
                return 5;
            }

            foreach (BrowserAgentIntegrityFrameProofV019 frame in proof.Frames)
            {
                string framePath = Path.Combine(sessionDirectory, frame.FileName);
                if (!File.Exists(framePath) ||
                    !string.Equals(HashFile(framePath), frame.FrameSha256, StringComparison.OrdinalIgnoreCase))
                {
                    report?.Invoke($"Integrity verification failed: frame {frame.Sequence} bytes changed or are missing.");
                    return 6;
                }
            }

            if (!string.IsNullOrWhiteSpace(proof.FinalImageFileName))
            {
                string finalPath = Path.Combine(sessionDirectory, proof.FinalImageFileName);
                if (!File.Exists(finalPath) ||
                    !string.Equals(HashFile(finalPath), proof.FinalImageSha256, StringComparison.OrdinalIgnoreCase) ||
                    !HasValidPngHeader(finalPath))
                {
                    report?.Invoke("Integrity verification failed: final PNG bytes changed, are missing, or no longer have a valid PNG header.");
                    return 7;
                }
            }

            report?.Invoke($"Integrity verification PASS: {proof.Frames.Count} frames, root={proof.FrameLedgerMerkleRootSha256}, final={proof.FinalImageSha256}.");
            return 0;
        }
        catch (Exception ex)
        {
            report?.Invoke("Integrity verification failed: " + ex.Message);
            return 99;
        }
    }

    public static bool SelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "LongCapture-v019-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frames"));
            byte[] a = Encoding.UTF8.GetBytes("frame-a");
            byte[] b = Encoding.UTF8.GetBytes("frame-b");
            byte[] c = Encoding.UTF8.GetBytes("frame-c");
            File.WriteAllBytes(Path.Combine(root, "frames", "frame-0001.png"), a);
            File.WriteAllBytes(Path.Combine(root, "frames", "frame-0002.png"), b);
            File.WriteAllBytes(Path.Combine(root, "frames", "frame-0003.png"), c);

            var f1 = TestFrame(1, "frames\\frame-0001.png", 0, 100, a);
            var f2 = TestFrame(2, "frames\\frame-0002.png", 80, 100, b);
            var f3 = TestFrame(3, "frames\\frame-0003.png", 160, 100, c);
            var clean = new BrowserAgentSessionManifest { Frames = new List<BrowserAgentFrameRecord> { f1, f2, f3 } };
            BrowserAgentIntegrityProofSummaryV019 cleanProof = FinalizePreStitch(root, clean);
            if (!cleanProof.Passed || cleanProof.CriticalFailures != 0 || cleanProof.CoverageGapCount != 0) return false;
            string cleanRoot = cleanProof.FrameLedgerMerkleRootSha256;

            // Same bytes at a different logical position are suspicious and a real gap is unresolved.
            File.WriteAllBytes(Path.Combine(root, "frames", "frame-0002.png"), a);
            StampRecapture(f2, a);
            f2.ScrollYCss = 80;
            f3.ScrollYCss = 300;
            BrowserAgentIntegrityProofSummaryV019 suspicious = FinalizePreStitch(root, clean);
            if (suspicious.Passed || suspicious.ExactDuplicateDifferentPositionCount < 1 || suspicious.CoverageGapCount < 1) return false;
            if (string.Equals(cleanRoot, suspicious.FrameLedgerMerkleRootSha256, StringComparison.OrdinalIgnoreCase)) return false;

            // Mutating a frame after its accepted digest was stamped must be a critical mismatch.
            File.WriteAllBytes(Path.Combine(root, "frames", "frame-0003.png"), Encoding.UTF8.GetBytes("tampered"));
            BrowserAgentIntegrityProofSummaryV019 tampered = FinalizePreStitch(root, clean);
            if (tampered.FrameHashMismatchCount < 1 || tampered.CriticalFailures < 1) return false;

            // Merkle construction is ordered and domain separated.
            byte[] leafA = HashLeaf(Encoding.UTF8.GetBytes("a"));
            byte[] leafB = HashLeaf(Encoding.UTF8.GetBytes("b"));
            string ab = Convert.ToHexString(ComputeMerkleRoot(new List<byte[]> { leafA, leafB }));
            string ba = Convert.ToHexString(ComputeMerkleRoot(new List<byte[]> { leafB, leafA }));
            if (string.Equals(ab, ba, StringComparison.Ordinal)) return false;
            return true;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static BrowserAgentFrameRecord TestFrame(int sequence, string fileName, double y, double viewportHeight, byte[] bytes)
    {
        var frame = new BrowserAgentFrameRecord
        {
            Sequence = sequence,
            FileName = fileName,
            ScrollYCss = y,
            ViewportHeightCss = viewportHeight,
            PixelWidth = 10,
            PixelHeight = 10,
            QualityState = "clean"
        };
        StampNewCapture(frame, bytes);
        return frame;
    }

    private static void AddFastRisk(BrowserAgentFrameRecord frame, int score, string reason)
    {
        frame.IntegrityProofSuspect = true;
        frame.IntegrityProofRiskScore += score;
        frame.IntegrityProofRiskReasons = string.IsNullOrWhiteSpace(frame.IntegrityProofRiskReasons)
            ? reason
            : frame.IntegrityProofRiskReasons + "," + reason;
    }

    private static string ComputeAnchorEvidenceHash(IReadOnlyList<BrowserAgentDomAnchorRecord> anchors)
    {
        if (anchors.Count == 0) return HashBytes(ReadOnlySpan<byte>.Empty);
        var builder = new StringBuilder();
        foreach (BrowserAgentDomAnchorRecord anchor in anchors.OrderBy(a => a.Key, StringComparer.Ordinal).ThenBy(a => a.DocumentYCss))
        {
            builder.Append(anchor.Key).Append('|')
                .Append(F(anchor.DocumentYCss)).Append('|')
                .Append(F(anchor.ViewportYCss)).Append('|')
                .Append(F(anchor.HeightCss)).Append('\n');
        }
        return HashBytes(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string CanonicalFrameLedgerEntry(BrowserAgentFrameRecord frame, string actualHash, long byteLength)
    {
        return string.Join("\n", new[]
        {
            "longcapture-browser-frame-proof-v1",
            "sequence=" + frame.Sequence.ToString(CultureInfo.InvariantCulture),
            "file=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(frame.FileName ?? string.Empty)),
            "frameSha256=" + actualHash,
            "bytes=" + byteLength.ToString(CultureInfo.InvariantCulture),
            "revision=" + frame.CaptureRevision.ToString(CultureInfo.InvariantCulture),
            "scrollY=" + F(frame.ScrollYCss),
            "viewportHeight=" + F(frame.ViewportHeightCss),
            "pixelWidth=" + frame.PixelWidth.ToString(CultureInfo.InvariantCulture),
            "pixelHeight=" + frame.PixelHeight.ToString(CultureInfo.InvariantCulture),
            "resolvedDeltaPixels=" + frame.ResolvedDeltaPixels.ToString(CultureInfo.InvariantCulture),
            "qualityState=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(frame.QualityState ?? string.Empty)),
            "integrityRisk=" + frame.IntegrityRiskScore.ToString(CultureInfo.InvariantCulture),
            "proofRisk=" + frame.IntegrityProofRiskScore.ToString(CultureInfo.InvariantCulture),
            "overlapVerified=" + (frame.OverlapVerified ? "1" : "0"),
            "anchorEvidenceSha256=" + frame.AnchorEvidenceSha256
        });
    }

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static byte[] HashLeaf(ReadOnlySpan<byte> data)
    {
        byte[] prefixed = new byte[data.Length + 1];
        prefixed[0] = 0x00;
        data.CopyTo(prefixed.AsSpan(1));
        return SHA256.HashData(prefixed);
    }

    private static byte[] HashNode(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        byte[] payload = new byte[1 + left.Length + right.Length];
        payload[0] = 0x01;
        left.CopyTo(payload.AsSpan(1));
        right.CopyTo(payload.AsSpan(1 + left.Length));
        return SHA256.HashData(payload);
    }

    private static byte[] ComputeMerkleRoot(IReadOnlyList<byte[]> leaves)
    {
        if (leaves.Count == 0) return SHA256.HashData(Array.Empty<byte>());
        return ComputeMerkleRootRange(leaves, 0, leaves.Count);
    }

    private static byte[] ComputeMerkleRootRange(IReadOnlyList<byte[]> leaves, int offset, int count)
    {
        if (count == 1) return leaves[offset];
        int split = LargestPowerOfTwoLessThan(count);
        byte[] left = ComputeMerkleRootRange(leaves, offset, split);
        byte[] right = ComputeMerkleRootRange(leaves, offset + split, count - split);
        return HashNode(left, right);
    }

    private static int LargestPowerOfTwoLessThan(int value)
    {
        int power = 1;
        while ((power << 1) < value) power <<= 1;
        return power;
    }

    private static bool HasValidPngHeader(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        if (stream.Read(header) != header.Length) return false;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        return header[..8].SequenceEqual(signature) &&
            header[12] == (byte)'I' && header[13] == (byte)'H' &&
            header[14] == (byte)'D' && header[15] == (byte)'R';
    }
}