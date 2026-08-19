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

    // v0.1.8: keep the center band as the scroll-geometry authority, but do not
    // let a moving/fixed object in the outer page rails disappear inside a median.
    public double LeftBandMeanAbsoluteError { get; init; }
    public double CenterBandMeanAbsoluteError { get; init; }
    public double RightBandMeanAbsoluteError { get; init; }
    public double LeftBandStrongDiffRatio { get; init; }
    public double CenterBandStrongDiffRatio { get; init; }
    public double RightBandStrongDiffRatio { get; init; }
    public bool LeftEdgeContamination { get; init; }
    public bool RightEdgeContamination { get; init; }
    public bool EdgeContamination => LeftEdgeContamination || RightEdgeContamination;

    public string Detail { get; init; } = string.Empty;
}

internal static class BrowserAgentOverlapVerifier
{
    internal const double MaxMeanAbsoluteError = 3.00;
    internal const double MaxStrongDiffRatio = 0.0125;
    private const int MaximumVisualCorrectionPixels = 72;
    private const double MinimumEdgeMaeForContamination = 4.50;
    private const double MinimumEdgeStrongForContamination = 0.025;

    private readonly record struct ScoreResult(
        int Delta,
        int Overlap,
        double Mae,
        double StrongRatio,
        double Composite,
        int Samples,
        double LeftMae,
        double CenterMae,
        double RightMae,
        double LeftStrong,
        double CenterStrong,
        double RightStrong);

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

        // A median of three bands intentionally protects scroll alignment from one animated
        // rail, but v0.1.7's real Linux.do evidence showed that the same median could hide a
        // sticky avatar that jumped in the left rail while the center text remained perfect.
        // Treat a rail as contaminated only when BOTH its absolute error and strong-difference
        // density are materially above the center band. This preserves normal document avatars
        // that move with the text while surfacing screen-space sticky/fixed transitions.
        double edgeMaeLimit = Math.Max(MinimumEdgeMaeForContamination, resolved.CenterMae * 3.5 + 1.0);
        double edgeStrongLimit = Math.Max(MinimumEdgeStrongForContamination, resolved.CenterStrong * 4.0 + 0.01);
        bool leftEdgeContamination =
            resolved.LeftMae > edgeMaeLimit && resolved.LeftStrong > edgeStrongLimit;
        bool rightEdgeContamination =
            resolved.RightMae > edgeMaeLimit && resolved.RightStrong > edgeStrongLimit;
        bool acceptable = visuallyStrong && correctionSafe && correctionConfident &&
            !leftEdgeContamination && !rightEdgeContamination;

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
            LeftBandMeanAbsoluteError = resolved.LeftMae,
            CenterBandMeanAbsoluteError = resolved.CenterMae,
            RightBandMeanAbsoluteError = resolved.RightMae,
            LeftBandStrongDiffRatio = resolved.LeftStrong,
            CenterBandStrongDiffRatio = resolved.CenterStrong,
            RightBandStrongDiffRatio = resolved.RightStrong,
            LeftEdgeContamination = leftEdgeContamination,
            RightEdgeContamination = rightEdgeContamination,
            Detail =
                $"mae={resolved.Mae:F3} strong={resolved.StrongRatio:P2} overlap={resolved.Overlap}px samples={resolved.Samples} " +
                $"expected={expectedDelta}px resolved={resolved.Delta}px correction={correction:+#;-#;0}px confidence={confidence:F3} " +
                $"bands=({resolved.LeftMae:F2}/{resolved.CenterMae:F2}/{resolved.RightMae:F2}) " +
                $"edgeContamination={(leftEdgeContamination ? "L" : "-")}{(rightEdgeContamination ? "R" : "-")}"
        };
    }

    private static ScoreResult Score(Bitmap previous, Bitmap current, int deltaPixels, bool dense)
    {
        int overlapPixels = Math.Min(previous.Height - deltaPixels, current.Height);
        if (deltaPixels < 0 || overlapPixels < 48)
        {
            return default;
        }

        // Include the outer rails. The old 7%-93% crop excluded Linux.do/Discourse sticky
        // avatars almost exactly where they live. Keep a tiny 2% trim for browser/page borders.
        int left = Math.Max(0, (int)Math.Round(previous.Width * 0.02));
        int right = Math.Min(previous.Width, (int)Math.Round(previous.Width * 0.98));
        int usableWidth = Math.Max(1, right - left);
        int xStep = dense ? Math.Max(4, previous.Width / 280) : Math.Max(10, previous.Width / 130);
        int yStep = dense ? Math.Max(2, overlapPixels / 96) : Math.Max(7, overlapPixels / 36);

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
                double normalizedX = (x - left) / (double)usableWidth;
                int band = normalizedX < 0.22 ? 0 : normalizedX > 0.78 ? 2 : 1;
                channelDifference[band] += dr + dg + db;
                if (mean > 30) strong[band]++;
                samples[band]++;
            }
        }

        double[] bandMae = new double[3];
        double[] bandStrong = new double[3];
        var maes = new List<double>(3);
        var strongRatios = new List<double>(3);
        int totalSamples = 0;
        for (int band = 0; band < 3; band++)
        {
            if (samples[band] <= 0) continue;
            bandMae[band] = channelDifference[band] / (samples[band] * 3.0);
            bandStrong[band] = strong[band] / (double)samples[band];
            maes.Add(bandMae[band]);
            strongRatios.Add(bandStrong[band]);
            totalSamples += samples[band];
        }
        if (maes.Count < 3) return default;

        maes.Sort();
        strongRatios.Sort();
        double medianMae = maes[1];
        double medianStrong = strongRatios[1];
        double composite = medianMae + medianStrong * 70.0;
        return new ScoreResult(
            deltaPixels,
            overlapPixels,
            medianMae,
            medianStrong,
            composite,
            totalSamples,
            bandMae[0],
            bandMae[1],
            bandMae[2],
            bandStrong[0],
            bandStrong[1],
            bandStrong[2]);
    }

    private static BrowserAgentOverlapCheck Reject(string detail, int expectedDelta = 0) => new()
    {
        Comparable = false,
        Acceptable = false,
        ExpectedDeltaPixels = expectedDelta,
        Detail = detail
    };
}
