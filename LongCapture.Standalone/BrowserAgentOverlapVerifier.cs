namespace LongCapture.Standalone;

internal sealed class BrowserAgentOverlapCheck
{
    public bool Comparable { get; init; }
    public bool Acceptable { get; init; }
    public double MeanAbsoluteError { get; init; }
    public double StrongDiffRatio { get; init; }
    public int OverlapPixels { get; init; }
    public int Samples { get; init; }
    public int ExpectedDeltaPixels { get; init; }
    public int ResolvedDeltaPixels { get; init; }
    public int VisualDeltaOffsetPixels { get; init; }
    public double AlignmentScore { get; init; }
    public double AlignmentConfidence { get; init; }
    public string Detail { get; init; } = string.Empty;
}

internal static class BrowserAgentOverlapVerifier
{
    internal const double MaxMeanAbsoluteError = 3.00;
    internal const double MaxStrongDiffRatio = 0.0125;
    private const int MaximumVisualCorrectionPixels = 72;

    private readonly record struct ScoreResult(
        int Delta,
        int Overlap,
        double Mae,
        double StrongRatio,
        double Composite,
        int Samples);

    public static BrowserAgentOverlapCheck Measure(
        string sessionDirectory,
        BrowserAgentFrameRecord previous,
        BrowserAgentFrameRecord current)
    {
        if (previous.PixelWidth <= 0 || previous.PixelHeight <= 0 ||
            current.PixelWidth != previous.PixelWidth || current.PixelHeight != previous.PixelHeight ||
            previous.ViewportHeightCss <= 0)
        {
            return Reject("frame metrics are not comparable");
        }

        double scaleY = previous.PixelHeight / previous.ViewportHeightCss;
        int expectedDelta = (int)Math.Round((current.ScrollYCss - previous.ScrollYCss) * scaleY);
        if (expectedDelta < 0 || expectedDelta >= previous.PixelHeight - 48)
        {
            return Reject($"insufficient expected overlap delta={expectedDelta}px", expectedDelta);
        }

        string previousPath = Path.Combine(sessionDirectory, previous.FileName);
        string currentPath = Path.Combine(sessionDirectory, current.FileName);
        using var previousBitmap = new Bitmap(previousPath);
        using var currentBitmap = new Bitmap(currentPath);

        if (previousBitmap.Width != currentBitmap.Width || previousBitmap.Height != currentBitmap.Height)
        {
            return Reject("PNG dimensions changed between adjacent frames", expectedDelta);
        }

        int searchRadius = Math.Min(
            MaximumVisualCorrectionPixels,
            Math.Max(18, (previousBitmap.Height - expectedDelta) / 5));
        int searchMin = Math.Max(1, expectedDelta - searchRadius);
        int searchMax = Math.Min(previousBitmap.Height - 49, expectedDelta + searchRadius);

        var candidates = new List<ScoreResult>();
        for (int delta = searchMin; delta <= searchMax; delta += 3)
        {
            candidates.Add(Score(previousBitmap, currentBitmap, delta, dense: false));
        }
        if ((expectedDelta - searchMin) % 3 != 0)
        {
            candidates.Add(Score(previousBitmap, currentBitmap, expectedDelta, dense: false));
        }

        ScoreResult coarseBest = candidates
            .Where(x => x.Samples > 0)
            .OrderBy(x => x.Composite + Math.Abs(x.Delta - expectedDelta) * 0.0025)
            .FirstOrDefault();
        if (coarseBest.Samples == 0)
        {
            return Reject("no visual-overlap samples", expectedDelta);
        }

        int refineMin = Math.Max(searchMin, coarseBest.Delta - 4);
        int refineMax = Math.Min(searchMax, coarseBest.Delta + 4);
        var refined = new List<ScoreResult>();
        for (int delta = refineMin; delta <= refineMax; delta++)
        {
            refined.Add(Score(previousBitmap, currentBitmap, delta, dense: true));
        }

        ScoreResult best = refined
            .Where(x => x.Samples > 0)
            .OrderBy(x => x.Composite + Math.Abs(x.Delta - expectedDelta) * 0.0015)
            .FirstOrDefault();
        ScoreResult expected = Score(previousBitmap, currentBitmap, expectedDelta, dense: true);
        if (best.Samples == 0 || expected.Samples == 0)
        {
            return Reject("visual-overlap refinement produced no samples", expectedDelta);
        }

        double secondScore = candidates
            .Where(x => x.Samples > 0 && Math.Abs(x.Delta - best.Delta) >= 6)
            .Select(x => x.Composite)
            .DefaultIfEmpty(best.Composite + 1.0)
            .Min();
        double confidence = Math.Max(0, secondScore - best.Composite) / Math.Max(0.25, best.Composite);

        bool expectedAlreadyStrong =
            expected.Mae <= MaxMeanAbsoluteError &&
            expected.StrongRatio <= MaxStrongDiffRatio;

        ScoreResult resolved = expectedAlreadyStrong && Math.Abs(best.Delta - expectedDelta) <= 6
            ? expected
            : best;

        int correction = resolved.Delta - expectedDelta;
        bool visuallyStrong =
            resolved.Mae <= MaxMeanAbsoluteError &&
            resolved.StrongRatio <= MaxStrongDiffRatio;
        bool correctionSafe = Math.Abs(correction) <= MaximumVisualCorrectionPixels;
        bool correctionConfident = correction == 0 || confidence >= 0.08 || expected.Mae > resolved.Mae + 0.35;
        bool acceptable = visuallyStrong && correctionSafe && correctionConfident;

        return new BrowserAgentOverlapCheck
        {
            Comparable = true,
            Acceptable = acceptable,
            MeanAbsoluteError = resolved.Mae,
            StrongDiffRatio = resolved.StrongRatio,
            OverlapPixels = resolved.Overlap,
            Samples = resolved.Samples,
            ExpectedDeltaPixels = expectedDelta,
            ResolvedDeltaPixels = resolved.Delta,
            VisualDeltaOffsetPixels = correction,
            AlignmentScore = resolved.Composite,
            AlignmentConfidence = confidence,
            Detail =
                $"mae={resolved.Mae:F3} strong={resolved.StrongRatio:P2} overlap={resolved.Overlap}px samples={resolved.Samples} " +
                $"expected={expectedDelta}px resolved={resolved.Delta}px correction={correction:+#;-#;0}px confidence={confidence:F3}"
        };
    }

    private static ScoreResult Score(Bitmap previous, Bitmap current, int deltaPixels, bool dense)
    {
        int overlapPixels = Math.Min(previous.Height - deltaPixels, current.Height);
        if (deltaPixels < 0 || overlapPixels < 48)
        {
            return default;
        }

        int left = Math.Max(0, (int)Math.Round(previous.Width * 0.07));
        int right = Math.Min(previous.Width, (int)Math.Round(previous.Width * 0.93));
        int usableWidth = Math.Max(1, right - left);
        int xStep = dense ? Math.Max(6, previous.Width / 220) : Math.Max(14, previous.Width / 110);
        int yStep = dense ? Math.Max(3, overlapPixels / 72) : Math.Max(8, overlapPixels / 30);

        long[] channelDifference = new long[3];
        int[] strong = new int[3];
        int[] samples = new int[3];

        for (int localY = 3; localY < overlapPixels - 3; localY += yStep)
        {
            int previousY = deltaPixels + localY;
            int currentY = localY;
            for (int x = left; x < right; x += xStep)
            {
                Color a = previous.GetPixel(x, previousY);
                Color b = current.GetPixel(x, currentY);
                int dr = Math.Abs(a.R - b.R);
                int dg = Math.Abs(a.G - b.G);
                int db = Math.Abs(a.B - b.B);
                int mean = (dr + dg + db) / 3;
                int band = Math.Min(2, Math.Max(0, (x - left) * 3 / usableWidth));
                channelDifference[band] += dr + dg + db;
                if (mean > 30) strong[band]++;
                samples[band]++;
            }
        }

        var maes = new List<double>(3);
        var strongRatios = new List<double>(3);
        int totalSamples = 0;
        for (int band = 0; band < 3; band++)
        {
            if (samples[band] <= 0) continue;
            maes.Add(channelDifference[band] / (samples[band] * 3.0));
            strongRatios.Add(strong[band] / (double)samples[band]);
            totalSamples += samples[band];
        }
        if (maes.Count < 2) return default;

        maes.Sort();
        strongRatios.Sort();
        double medianMae = maes[maes.Count / 2];
        double medianStrong = strongRatios[strongRatios.Count / 2];
        double composite = medianMae + medianStrong * 70.0;
        return new ScoreResult(deltaPixels, overlapPixels, medianMae, medianStrong, composite, totalSamples);
    }

    private static BrowserAgentOverlapCheck Reject(string detail, int expectedDelta = 0) => new()
    {
        Comparable = false,
        Acceptable = false,
        ExpectedDeltaPixels = expectedDelta,
        Detail = detail
    };
}
