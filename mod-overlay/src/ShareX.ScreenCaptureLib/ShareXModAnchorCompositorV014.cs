#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// Deterministic v0.1.4 compositor used when the multi-anchor matcher has already established a
/// trustworthy vertical scroll delta. Unlike the legacy exact-row compositor, it never guesses an
/// unrelated matchIndex/ignoreBottomOffset pair: it preserves the current mosaic and appends exactly
/// the newly exposed bottom delta. This keeps frame-to-mosaic coordinates stable, which also makes
/// deferred fixed-overlay repair meaningful.
/// </summary>
internal static class ShareXModAnchorCompositorV014
{
    public static Bitmap? TryAppend(Bitmap? result, Bitmap currentFrame, int scrollDelta)
    {
        if (currentFrame is null) return null;
        if (result is null) return (Bitmap)currentFrame.Clone();
        if (result.Width != currentFrame.Width) return null;
        if (scrollDelta <= 0 || scrollDelta >= currentFrame.Height) return null;

        Bitmap combined = new(result.Width, checked(result.Height + scrollDelta), PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(combined);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.None;
        graphics.DrawImageUnscaled(result, 0, 0);
        graphics.DrawImage(
            currentFrame,
            new Rectangle(0, result.Height, currentFrame.Width, scrollDelta),
            new Rectangle(0, currentFrame.Height - scrollDelta, currentFrame.Width, scrollDelta),
            GraphicsUnit.Pixel);
        return combined;
    }
}
