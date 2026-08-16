[CmdletBinding()]
param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$repoRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repoRoot) { throw "Not inside a Git repository." }

function Replace-Literal {
    param([string]$Path,[string]$Old,[string]$New,[string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { throw "RC6 target missing: $Path" }
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains($Marker)) {
        Write-Host "[v0.1.9-rc6] already present: $Marker" -ForegroundColor DarkYellow
        return
    }
    if (-not $text.Contains($Old)) { throw "RC6 anchor missing: $Marker in $Path" }
    if (-not $CheckOnly) {
        $text = $text.Replace($Old,$New)
        [IO.File]::WriteAllText($Path,$text,[Text.UTF8Encoding]::new($true))
    }
    Write-Host "[v0.1.9-rc6] applied/compatible: $Marker" -ForegroundColor Cyan
}

$matcher = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModVerticalFallbackMatcher.cs"
$resolver = Join-Path $repoRoot "mod-overlay\src\ShareX.ScreenCaptureLib\ShareXModTransitionResolverV019.cs"
$automation = Join-Path $repoRoot "LongCapture.Standalone\AutomationTestRunner.cs"

# RC5 still allowed a sparse white page to fool the fixed-phase coarse sampler. In the real RC5
# Linux.do run the true last movement was 750px and exact validation scored 0.0, but the near search
# encountered a different zero-score candidate first. Seed the restricted search with the temporal
# prior itself and require a material score improvement before moving away from it. Equal/near-equal
# sparse-background scores therefore keep the prior rather than whichever coarse phase appears first.
Replace-Literal -Path $matcher `
  -Old @'
        PixelBuffer previous = PixelBuffer.FromBitmap(previousFrame);
        PixelBuffer current = PixelBuffer.FromBitmap(currentFrame);
        try
        {
            const int coarse = 8;
            for (int delta = minDelta; delta <= maxDelta; delta += coarse)
            {
                double candidate = CalculateScore(previous, current, previousFrame.Width, previousFrame.Height, delta, 20, 18);
                if (candidate < bestScore) { bestScore = candidate; bestDelta = delta; }
            }
            if (bestDelta == 0) return false;
            int refineStart = Math.Max(minDelta, bestDelta - coarse);
            int refineEnd = Math.Min(maxDelta, bestDelta + coarse);
            for (int delta = refineStart; delta <= refineEnd; delta++)
            {
                double candidate = CalculateScore(previous, current, previousFrame.Width, previousFrame.Height, delta, 16, 16);
                if (candidate < bestScore) { bestScore = candidate; bestDelta = delta; }
            }
            return bestScore <= settings.FallbackMaxMeanDifference;
        }
'@ `
  -New @'
        PixelBuffer previous = PixelBuffer.FromBitmap(previousFrame);
        PixelBuffer current = PixelBuffer.FromBitmap(currentFrame);
        try
        {
            const double sparseTieEpsilon = 0.05;
            const int coarse = 8;

            // RC6: always score the trusted temporal prior first. Sparse pages can make several
            // displacements score exactly zero at one sampling phase; a tie is not evidence that
            // the first coarse candidate is better than the prior.
            bestDelta = priorDelta;
            bestScore = CalculateScore(previous, current, previousFrame.Width, previousFrame.Height, priorDelta, 16, 16);

            for (int delta = minDelta; delta <= maxDelta; delta += coarse)
            {
                double candidate = CalculateScore(previous, current, previousFrame.Width, previousFrame.Height, delta, 20, 18);
                if (candidate + sparseTieEpsilon < bestScore ||
                    (Math.Abs(candidate - bestScore) <= sparseTieEpsilon &&
                     Math.Abs(delta - priorDelta) < Math.Abs(bestDelta - priorDelta)))
                {
                    bestScore = candidate;
                    bestDelta = delta;
                }
            }
            if (bestDelta == 0) return false;
            int refineStart = Math.Max(minDelta, bestDelta - coarse);
            int refineEnd = Math.Min(maxDelta, bestDelta + coarse);
            for (int delta = refineStart; delta <= refineEnd; delta++)
            {
                double candidate = CalculateScore(previous, current, previousFrame.Width, previousFrame.Height, delta, 16, 16);
                if (candidate + sparseTieEpsilon < bestScore ||
                    (Math.Abs(candidate - bestScore) <= sparseTieEpsilon &&
                     Math.Abs(delta - priorDelta) < Math.Abs(bestDelta - priorDelta)))
                {
                    bestScore = candidate;
                    bestDelta = delta;
                }
            }
            return bestScore <= settings.FallbackMaxMeanDifference;
        }
'@ `
  -Marker 'const double sparseTieEpsilon = 0.05;'

# Add an independent content-bearing support measure. The legacy mean score can be dominated by
# blank white background. This metric samples only high-gradient pixels (text/edges/controls) in the
# central content band, then asks how many of those informative samples actually line up after the
# candidate translation. It is not a new full-range search; it is secondary evidence for comparing a
# small number of already-proposed deltas.
Replace-Literal -Path $matcher `
  -Old '    private static bool Compatible(Bitmap a, Bitmap b) => a != null && b != null && a.Width == b.Width && a.Height == b.Height;' `
  -New @'
    internal static bool TryMeasureInformativeSupport(
        Bitmap previousFrame,
        Bitmap currentFrame,
        int delta,
        out double support,
        out int informativeSamples)
    {
        support = 0;
        informativeSamples = 0;
        if (!Compatible(previousFrame, currentFrame) || delta <= 0 || delta >= currentFrame.Height) return false;

        int width = currentFrame.Width;
        int height = currentFrame.Height;
        int overlap = height - delta;
        if (overlap < Math.Max(64, height / 8)) return false;
        int marginX = Math.Min(width / 3, Math.Max(40, width / 6));
        if (width - marginX * 2 < 96) return false;

        PixelBuffer previous = PixelBuffer.FromBitmap(previousFrame);
        PixelBuffer current = PixelBuffer.FromBitmap(currentFrame);
        try
        {
            const int step = 6;
            const int gradientThreshold = 36;
            const int matchDifferenceThreshold = 15; // RGB sum: mean absolute channel error <= 5.
            int matches = 0;

            for (int y = 0; y + 1 < overlap; y += step)
            {
                int previousY = y + delta;
                if (previousY + 1 >= height) break;
                for (int x = marginX; x + 1 < width - marginX; x += step)
                {
                    int currentOffset = current.RowOffset(y) + x * 4;
                    int currentRight = currentOffset + 4;
                    int currentDown = current.RowOffset(y + 1) + x * 4;
                    int previousOffset = previous.RowOffset(previousY) + x * 4;
                    int previousRight = previousOffset + 4;
                    int previousDown = previous.RowOffset(previousY + 1) + x * 4;

                    int currentGradient =
                        ColorDifference(current.Bytes, currentOffset, current.Bytes, currentRight) +
                        ColorDifference(current.Bytes, currentOffset, current.Bytes, currentDown);
                    int previousGradient =
                        ColorDifference(previous.Bytes, previousOffset, previous.Bytes, previousRight) +
                        ColorDifference(previous.Bytes, previousOffset, previous.Bytes, previousDown);
                    if (Math.Max(currentGradient, previousGradient) < gradientThreshold) continue;

                    informativeSamples++;
                    if (ColorDifference(previous.Bytes, previousOffset, current.Bytes, currentOffset) <= matchDifferenceThreshold)
                        matches++;
                }
            }

            if (informativeSamples < 64) return false;
            support = matches / (double)informativeSamples;
            return true;
        }
        finally
        {
            previous.Dispose();
            current.Dispose();
        }
    }

    private static bool Compatible(Bitmap a, Bitmap b) => a != null && b != null && a.Width == b.Width && a.Height == b.Height;
'@ `
  -Marker 'internal static bool TryMeasureInformativeSupport('

# A near-prior direct anchor is not automatically correct merely because it is within the large
# temporal tolerance. RC5's real run accepted 712px inside a ~750px cluster; the output therefore
# lost 38 document rows even before the final stop. Let content-bearing edge evidence prefer the
# validated prior when it decisively aligns more real text/edges than the direct candidate.
Replace-Literal -Path $resolver `
  -Old @'
                    if (nearPrior)
                    {
                        // RC2 real evidence: prior ~= 750, direct=748, agreement=3, anchor score=4.38.
'@ `
  -New @'
                    if (nearPrior)
                    {
                        if (TryPreferValidatedPriorByInformativeSupport(
                                previousReliable, current, settings, prior, directAnchor.ScrollDelta,
                                out double informativePriorScore,
                                out double informativePriorSupport,
                                out double informativeDirectSupport))
                        {
                            delta = prior;
                            score = informativePriorScore;
                            source = "informative-prior-overrode-near-direct-v019";
                            priorResolved++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }

                        // RC2 real evidence: prior ~= 750, direct=748, agreement=3, anchor score=4.38.
'@ `
  -Marker 'source = "informative-prior-overrode-near-direct-v019";'

# After the strict genuine-short-motion escape hatch has had its chance, use informative support as
# a third independent witness. This handles the new RC5 frame-44 failure where exact prior=750 was
# perfect but blank-background coarse sampling produced false 428/657 hypotheses.
Replace-Literal -Path $resolver `
  -Old @'
                // A robust prior score can itself be a repeated-pattern alias, so disagreement still
                // matters. RC5 makes the rule symmetric and evidence-aware: after the strict short-
'@ `
  -New @'
                if (priorValid && hasFull && !AreIndependentCandidatesConsistent(fullDelta, prior) &&
                    TryPreferInformativePriorOverCandidate(
                        previousReliable, current, prior, fullDelta,
                        out double sparsePriorSupport, out double sparseFullSupport))
                {
                    delta = prior;
                    score = priorScore;
                    source = "informative-prior-overrode-sparse-full-range-alias-v019";
                    priorResolved++;
                    Remember(delta);
                    Complete(delta, source, true);
                    return true;
                }

                // A robust prior score can itself be a repeated-pattern alias, so disagreement still
                // matters. RC5 makes the rule symmetric and evidence-aware: after the strict short-
'@ `
  -Marker 'source = "informative-prior-overrode-sparse-full-range-alias-v019";'

# Helpers are intentionally conservative: require enough informative samples, strong absolute prior
# support, and both absolute and relative separation from the competing candidate.
Replace-Literal -Path $resolver `
  -Old '    private static bool IsSafeUncorroboratedPriorCandidate(int candidateDelta, int priorDelta)' `
  -New @'
    internal static bool ShouldPreferInformativePrior(
        double priorSupport, int priorSamples, double candidateSupport, int candidateSamples)
    {
        if (priorSamples < 64 || candidateSamples < 64) return false;
        if (double.IsNaN(priorSupport) || double.IsNaN(candidateSupport)) return false;
        if (priorSupport < 0.34) return false;
        return priorSupport >= candidateSupport + 0.12 &&
               priorSupport >= candidateSupport * 1.30;
    }

    private static bool TryPreferInformativePriorOverCandidate(
        Bitmap previous,
        Bitmap current,
        int priorDelta,
        int candidateDelta,
        out double priorSupport,
        out double candidateSupport)
    {
        priorSupport = candidateSupport = 0;
        bool priorOk = ShareXModVerticalFallbackMatcher.TryMeasureInformativeSupport(
            previous, current, priorDelta, out priorSupport, out int priorSamples);
        bool candidateOk = ShareXModVerticalFallbackMatcher.TryMeasureInformativeSupport(
            previous, current, candidateDelta, out candidateSupport, out int candidateSamples);
        return priorOk && candidateOk &&
               ShouldPreferInformativePrior(priorSupport, priorSamples, candidateSupport, candidateSamples);
    }

    private static bool TryPreferValidatedPriorByInformativeSupport(
        Bitmap previous,
        Bitmap current,
        ShareXModRobustScrollingSettings settings,
        int priorDelta,
        int candidateDelta,
        out double priorScore,
        out double priorSupport,
        out double candidateSupport)
    {
        priorScore = double.MaxValue;
        priorSupport = candidateSupport = 0;
        if (priorDelta <= 0 || candidateDelta <= 0 || priorDelta == candidateDelta) return false;
        if (!ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                previous, current, settings, priorDelta, out priorScore)) return false;
        return TryPreferInformativePriorOverCandidate(
            previous, current, priorDelta, candidateDelta, out priorSupport, out candidateSupport);
    }

    private static bool IsSafeUncorroboratedPriorCandidate(int candidateDelta, int priorDelta)
'@ `
  -Marker 'internal static bool ShouldPreferInformativePrior('

# Wire the RC6 evidence suite into QUICK/DEEP acceptance.
Replace-Literal -Path $automation `
  -Old '        "ShareX.ScreenCaptureLib.ShareXModV019Rc5RealEvidenceSelfTests",' `
  -New @'
        "ShareX.ScreenCaptureLib.ShareXModV019Rc5RealEvidenceSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModV019Rc6RealEvidenceSelfTests",
'@ `
  -Marker '"ShareX.ScreenCaptureLib.ShareXModV019Rc6RealEvidenceSelfTests",'

if ($CheckOnly) {
    Write-Host "LongCapture v0.1.9 RC6 sparse-content edge-evidence compatibility passed." -ForegroundColor Green
} else {
    Write-Host "LongCapture v0.1.9 RC6 sparse-content edge evidence + near-prior de-jitter applied." -ForegroundColor Green
}
