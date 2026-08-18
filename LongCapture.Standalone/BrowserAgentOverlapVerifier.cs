namespace LongCapture.Standalone;

internal sealed class BrowserAgentOverlapCheck
{
    public bool Comparable { get; init; }
    public bool Acceptable { get; init; }
    public double MeanAbsoluteError { get; init; }
    public double StrongDiffRatio { get; init; }
    public int OverlapPixels { get; init; }
    public int Samples { get; init; }
    public string Detail { get; init; } = string.Empty;
}

internal static class BrowserAgentOverlapVerifier
{
    internal const double MaxMeanAbsoluteError = 4.50;
    internal const double MaxStrongDiffRatio = 0.020;

    public static BrowserAgentOverlapCheck Measure(
        string sessionDirectory,
        BrowserAgentFrameRecord previous,
        BrowserAgentFrameRecord current)
    {
        if (previous.PixelWidth <= 0 || previous.PixelHeight <= 0 ||
            current.PixelWidth != previous.PixelWidth || current.PixelHeight <= 0 ||
            previous.ViewportHeightCss <= 0)
        {
            return new BrowserAgentOverlapCheck
            {
                Comparable = false,
                Acceptable = false,
                Detail = "frame metrics are not comparable"
            };
        }

        double scaleY = previous.PixelHeight / previous.ViewportHeightCss;
        int deltaPixels = (int)Math.Round((current.ScrollYCss - previous.ScrollYCss) * scaleY);
        int overlapPixels = Math.Min(previous.PixelHeight - deltaPixels, current.PixelHeight);
        if (deltaPixels < 0 || overlapPixels < 48)
        {
            return new BrowserAgentOverlapCheck
            {
                Comparable = false,
                Acceptable = false,
                OverlapPixels = Math.Max(0, overlapPixels),
                Detail = $"insufficient overlap delta={deltaPixels}px overlap={overlapPixels}px"
            };
        }

        string previousPath = Path.Combine(sessionDirectory, previous.FileName);
        string currentPath = Path.Combine(sessionDirectory, current.FileName);
        using var previousBitmap = new Bitmap(previousPath);
        using var currentBitmap = new Bitmap(currentPath);

        int left = Math.Max(0, (int)Math.Round(previousBitmap.Width * 0.06));
        int right = Math.Min(previousBitmap.Width, (int)Math.Round(previousBitmap.Width * 0.94));
        int xStep = Math.Max(4, previousBitmap.Width / 240);
        int yStep = Math.Max(2, overlapPixels / 80);

        long channelDifference = 0;
        int strong = 0;
        int samples = 0;

        for (int localY = 2; localY < overlapPixels - 2; localY += yStep)
        {
            int previousY = deltaPixels + localY;
            int currentY = localY;
            for (int x = left; x < right; x += xStep)
            {
                Color a = previousBitmap.GetPixel(x, previousY);
                Color b = currentBitmap.GetPixel(x, currentY);
                int dr = Math.Abs(a.R - b.R);
                int dg = Math.Abs(a.G - b.G);
                int db = Math.Abs(a.B - b.B);
                int mean = (dr + dg + db) / 3;
                channelDifference += dr + dg + db;
                if (mean > 30) strong++;
                samples++;
            }
        }

        if (samples == 0)
        {
            return new BrowserAgentOverlapCheck
            {
                Comparable = false,
                Acceptable = false,
                OverlapPixels = overlapPixels,
                Detail = "no overlap samples"
            };
        }

        double mae = channelDifference / (samples * 3.0);
        double strongRatio = strong / (double)samples;
        bool acceptable = mae <= MaxMeanAbsoluteError && strongRatio <= MaxStrongDiffRatio;

        return new BrowserAgentOverlapCheck
        {
            Comparable = true,
            Acceptable = acceptable,
            MeanAbsoluteError = mae,
            StrongDiffRatio = strongRatio,
            OverlapPixels = overlapPixels,
            Samples = samples,
            Detail = $"mae={mae:F3} strong={strongRatio:P2} overlap={overlapPixels}px samples={samples}"
        };
    }
}
