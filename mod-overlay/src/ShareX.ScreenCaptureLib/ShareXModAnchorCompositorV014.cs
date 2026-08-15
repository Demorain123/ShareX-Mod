#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModAnchorCompositorTelemetry(
    int AppendCount,
    int DetectedStationaryTiles,
    int RepairedStationaryTiles,
    int PendingTailTiles,
    int LatestScrollDelta,
    int LatestViewportHeight,
    bool LowOverlapRisk);

/// <summary>
/// Anchor-first compositor. v0.1.5 keeps the deterministic delta geometry introduced in v0.1.4,
/// but also repairs fixed/sticky UI that intersects the bottom append strip.
///
/// Geometry of the repair:
///   previous viewport pixel y represents logical (previousOffset + y)
///   after scrolling by delta, that same logical pixel is visible at current (y - delta)
///
/// A fixed overlay instead remains near the same viewport y. We classify tiles by comparing both
/// hypotheses at low resolution. When the stationary hypothesis wins strongly, the previous mosaic
/// tile is repaired from current(y-delta) before the new bottom strip is appended. Therefore each
/// fixed bottom control is removed one frame later; only the newest tail can remain at manual stop.
/// </summary>
internal static class ShareXModAnchorCompositorV014
{
    private const int AnalysisScale = 4;
    private const int TileWidthSmall = 8;
    private const int TileHeightSmall = 6;
    private static readonly object TelemetrySync = new();

    private static int appendCount;
    private static int detectedStationaryTiles;
    private static int repairedStationaryTiles;
    private static int pendingTailTiles;
    private static int latestScrollDelta;
    private static int latestViewportHeight;
    private static bool lowOverlapRisk;

    public static ShareXModAnchorCompositorTelemetry SnapshotTelemetry()
    {
        lock (TelemetrySync)
        {
            return new ShareXModAnchorCompositorTelemetry(
                appendCount,
                detectedStationaryTiles,
                repairedStationaryTiles,
                pendingTailTiles,
                latestScrollDelta,
                latestViewportHeight,
                lowOverlapRisk);
        }
    }

    public static Bitmap? TryAppend(Bitmap? result, Bitmap currentFrame, int scrollDelta)
    {
        if (currentFrame is null) return null;
        if (result is null)
        {
            ResetTelemetry(currentFrame.Height, 0);
            return (Bitmap)currentFrame.Clone();
        }
        if (result.Width != currentFrame.Width) return null;
        if (scrollDelta <= 0 || scrollDelta >= currentFrame.Height) return null;

        if (result.Height <= currentFrame.Height)
        {
            ResetTelemetry(currentFrame.Height, scrollDelta);
        }

        Bitmap combined = new(result.Width, checked(result.Height + scrollDelta), PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(combined);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.None;
        graphics.DrawImageUnscaled(result, 0, 0);

        FixedOverlayRepair repair = RepairPreviousBottomFixedTiles(
            graphics,
            result,
            currentFrame,
            scrollDelta);

        graphics.DrawImage(
            currentFrame,
            new Rectangle(0, result.Height, currentFrame.Width, scrollDelta),
            new Rectangle(0, currentFrame.Height - scrollDelta, currentFrame.Width, scrollDelta),
            GraphicsUnit.Pixel);

        ShareXModAnchorCompositorTelemetry telemetry;
        lock (TelemetrySync)
        {
            appendCount++;
            detectedStationaryTiles += repair.DetectedTiles;
            repairedStationaryTiles += repair.RepairedTiles;
            // A stationary control detected in the previous viewport is expected to exist again in
            // the just-appended current tail. It becomes repairable on the next frame.
            pendingTailTiles = repair.DetectedTiles;
            latestScrollDelta = scrollDelta;
            latestViewportHeight = currentFrame.Height;
            lowOverlapRisk = scrollDelta > currentFrame.Height / 2;
            telemetry = SnapshotTelemetryUnsafe();
        }

        TryWriteEvidence(telemetry, repair.MaskSmall);
        repair.MaskSmall?.Dispose();
        return combined;
    }

    private static FixedOverlayRepair RepairPreviousBottomFixedTiles(
        Graphics destinationGraphics,
        Bitmap result,
        Bitmap currentFrame,
        int scrollDelta)
    {
        int viewportHeight = currentFrame.Height;
        int previousViewportTop = result.Height - viewportHeight;
        if (previousViewportTop < 0) return FixedOverlayRepair.Empty;

        // Only the previous frame's newly appended band can contain a repeated fixed control.
        // y >= delta is additionally required because current(y-delta) is the recovery source.
        int repairTop = Math.Max(viewportHeight - scrollDelta, scrollDelta);
        if (repairTop >= viewportHeight) return FixedOverlayRepair.Empty;

        int smallWidth = Math.Max(1, (currentFrame.Width + AnalysisScale - 1) / AnalysisScale);
        int smallHeight = Math.Max(1, (viewportHeight + AnalysisScale - 1) / AnalysisScale);
        int smallDelta = Math.Max(1, (int)Math.Round(scrollDelta / (double)AnalysisScale));
        int smallRepairTop = Math.Max(0, repairTop / AnalysisScale);

        using Bitmap previousSmall = new(smallWidth, smallHeight, PixelFormat.Format24bppRgb);
        using Bitmap currentSmall = new(smallWidth, smallHeight, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(previousSmall))
        {
            g.InterpolationMode = InterpolationMode.Low;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(
                result,
                new Rectangle(0, 0, smallWidth, smallHeight),
                new Rectangle(0, previousViewportTop, currentFrame.Width, viewportHeight),
                GraphicsUnit.Pixel);
        }
        using (Graphics g = Graphics.FromImage(currentSmall))
        {
            g.InterpolationMode = InterpolationMode.Low;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(currentFrame, new Rectangle(0, 0, smallWidth, smallHeight));
        }

        int columns = (smallWidth + TileWidthSmall - 1) / TileWidthSmall;
        int rows = (smallHeight + TileHeightSmall - 1) / TileHeightSmall;
        bool[,] strong = new bool[rows, columns];
        bool[,] mild = new bool[rows, columns];
        var metrics = new TileMetric[rows, columns];

        for (int row = 0; row < rows; row++)
        {
            int sy = row * TileHeightSmall;
            if (sy < smallRepairTop || sy - smallDelta < 0) continue;
            int th = Math.Min(TileHeightSmall, smallHeight - sy);
            int recoveryY = sy - smallDelta;
            if (recoveryY + th > smallHeight) continue;

            for (int column = 0; column < columns; column++)
            {
                int sx = column * TileWidthSmall;
                int tw = Math.Min(TileWidthSmall, smallWidth - sx);
                double sameError = MeanAbsoluteError(previousSmall, sx, sy, currentSmall, sx, sy, tw, th);
                double recoveryError = MeanAbsoluteError(previousSmall, sx, sy, currentSmall, sx, recoveryY, tw, th);
                metrics[row, column] = new TileMetric(sameError, recoveryError);

                strong[row, column] =
                    sameError <= 24.0 &&
                    recoveryError >= 30.0 &&
                    recoveryError >= sameError * 1.55 + 6.0;

                mild[row, column] =
                    sameError <= 52.0 &&
                    recoveryError >= 32.0 &&
                    recoveryError >= sameError * 1.28 + 8.0;
            }
        }

        // Dynamic counters/icons can make the center tile differ while the button shell remains
        // stationary. Allow one-tile dilation, but only into tiles that still favor stationarity.
        bool[,] selected = (bool[,])strong.Clone();
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                if (!mild[row, column] || selected[row, column]) continue;
                if (HasStrongNeighbor(strong, row, column)) selected[row, column] = true;
            }
        }

        int detected = 0;
        int repaired = 0;
        Bitmap? mask = null;
        Graphics? maskGraphics = null;
        try
        {
            for (int row = 0; row < rows; row++)
            {
                int sy = row * TileHeightSmall;
                for (int column = 0; column < columns; column++)
                {
                    if (!selected[row, column]) continue;
                    detected++;

                    int sx = column * TileWidthSmall;
                    int x = sx * AnalysisScale;
                    int y = sy * AnalysisScale;
                    int width = Math.Min(TileWidthSmall * AnalysisScale, currentFrame.Width - x);
                    int height = Math.Min(TileHeightSmall * AnalysisScale, viewportHeight - y);
                    int sourceY = y - scrollDelta;
                    if (width <= 0 || height <= 0 || sourceY < 0 || sourceY + height > viewportHeight) continue;

                    destinationGraphics.DrawImage(
                        currentFrame,
                        new Rectangle(x, previousViewportTop + y, width, height),
                        new Rectangle(x, sourceY, width, height),
                        GraphicsUnit.Pixel);
                    repaired++;

                    if (mask is null)
                    {
                        mask = new Bitmap(smallWidth, smallHeight, PixelFormat.Format24bppRgb);
                        maskGraphics = Graphics.FromImage(mask);
                        maskGraphics.Clear(Color.Black);
                    }
                    maskGraphics!.FillRectangle(Brushes.White, sx, sy,
                        Math.Min(TileWidthSmall, smallWidth - sx),
                        Math.Min(TileHeightSmall, smallHeight - sy));
                }
            }
        }
        finally
        {
            maskGraphics?.Dispose();
        }

        return new FixedOverlayRepair(detected, repaired, mask);
    }

    private static bool HasStrongNeighbor(bool[,] strong, int row, int column)
    {
        int rows = strong.GetLength(0);
        int columns = strong.GetLength(1);
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int y = row + dy;
                int x = column + dx;
                if (y >= 0 && y < rows && x >= 0 && x < columns && strong[y, x]) return true;
            }
        }
        return false;
    }

    private static double MeanAbsoluteError(
        Bitmap a, int ax, int ay,
        Bitmap b, int bx, int by,
        int width, int height)
    {
        long total = 0;
        long samples = 0;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                Color ca = a.GetPixel(ax + x, ay + y);
                Color cb = b.GetPixel(bx + x, by + y);
                total += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
                samples += 3;
            }
        }
        return samples == 0 ? 0.0 : total / (double)samples;
    }

    private static void ResetTelemetry(int viewportHeight, int delta)
    {
        lock (TelemetrySync)
        {
            appendCount = 0;
            detectedStationaryTiles = 0;
            repairedStationaryTiles = 0;
            pendingTailTiles = 0;
            latestScrollDelta = delta;
            latestViewportHeight = viewportHeight;
            lowOverlapRisk = false;
        }
    }

    private static ShareXModAnchorCompositorTelemetry SnapshotTelemetryUnsafe() =>
        new(
            appendCount,
            detectedStationaryTiles,
            repairedStationaryTiles,
            pendingTailTiles,
            latestScrollDelta,
            latestViewportHeight,
            lowOverlapRisk);

    private static void TryWriteEvidence(ShareXModAnchorCompositorTelemetry telemetry, Bitmap? mask)
    {
        try
        {
            string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
            if (string.IsNullOrWhiteSpace(root)) return;
            string directory = Path.Combine(root, "anchor-compositor-v015");
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("anchor-compositor-v015", directory);

            string line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                telemetry.AppendCount,
                telemetry.DetectedStationaryTiles,
                telemetry.RepairedStationaryTiles,
                telemetry.PendingTailTiles,
                telemetry.LatestScrollDelta,
                telemetry.LatestViewportHeight,
                telemetry.LowOverlapRisk
            });
            File.AppendAllText(Path.Combine(directory, "fixed-overlay-evidence.jsonl"), line + Environment.NewLine, new UTF8Encoding(false));

            if (mask is not null)
            {
                mask.Save(Path.Combine(directory, $"fixed-overlay-mask-{telemetry.AppendCount:D3}.png"), ImageFormat.Png);
            }
        }
        catch
        {
            // Evidence is advisory and must never invalidate a capture.
        }
    }

    private readonly record struct TileMetric(double SameError, double RecoveryError);

    private sealed record FixedOverlayRepair(int DetectedTiles, int RepairedTiles, Bitmap? MaskSmall)
    {
        public static FixedOverlayRepair Empty { get; } = new(0, 0, null);
    }
}
