#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// v0.1.8 compatibility smoke for the retired v0.1.6 delayed-repair engine.
///
/// v0.1.6 used this suite as an acceptance gate requiring repeated fixed controls to be erased from
/// raster output. That policy is intentionally retired in v0.1.8 because the same classifier can
/// overwrite legitimate document pixels. The class remains in the suite so old code still gets a
/// small compile/runtime smoke, while v0.1.8 output correctness is owned by
/// ShareXModV018TrustSplitSelfTests.
/// </summary>
internal static class ShareXModV016ReplaySelfTests
{
    public static string RunOrThrow()
    {
        const int width = 320;
        const int height = 240;
        const int delta = 80;

        using Bitmap previous = BuildViewport(width, height, 0);
        using Bitmap current = BuildViewport(width, height, delta);
        using Bitmap result = (Bitmap)previous.Clone();
        using var session = new ShareXModDelayedCompositorV016.Session();
        using Bitmap? combined = session.TryAppend(result, previous, current, delta);

        if (combined is null)
            throw new InvalidOperationException("Retired v0.1.6 compositor failed its compatibility smoke.");
        if (combined.Width != width || combined.Height != height + delta)
            throw new InvalidOperationException($"Retired v0.1.6 compositor geometry mismatch: {combined.Size}.");

        return "v0.1.6 compatibility smoke passed; destructive fixed-control removal acceptance is retired in v0.1.8 and replaced by immutable-mosaic trust-split regression.";
    }

    private static Bitmap BuildViewport(int width, int height, int logicalTop)
    {
        Bitmap bitmap = new(width, height);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        for (int y = 0; y < height; y += 8)
        {
            int row = (logicalTop + y) / 8;
            using var brush = new SolidBrush(Color.FromArgb(
                40 + row * 17 % 150,
                55 + row * 19 % 145,
                70 + row * 23 % 140));
            g.FillRectangle(brush, 18 + row * 13 % 180, y + 2, 60 + row % 50, 3);
        }
        return bitmap;
    }
}
