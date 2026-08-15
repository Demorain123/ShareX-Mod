#nullable enable

using System;
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
/// Anchor-first compositor. v0.1.5 keeps deterministic delta geometry and adds deferred recovery for
/// fixed/sticky UI that intersects the newly appended bottom strip.
///
/// For a previous viewport tile at y, two hypotheses are compared:
///  - fixed/stationary: previous(y) ~= current(y)
///  - document motion:  previous(y) ~= current(y-delta)
///
/// A strongly stationary tile in the previous append band is repaired from current(y-delta), which
/// is the same logical document location after the known anchor delta. The repair happens before the
/// newest bottom strip is appended, so repeated fixed controls are removed one frame later. Only the
/// newest tail can remain when the user manually stops.
/// </summary>
internal static class ShareXModAnchorCompositorV014
{
    private const int TileWidth = 40;
    private const int TileHeight = 32;
    private const int SampleStep = 4;
    private const int EvidenceScale = 4;
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
            return SnapshotTelemetryUnsafe();
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

        // Only pixels that were inside the previous newly appended strip can have been stamped into
        // the mosaic by a fixed control. current(y-delta) must also exist to recover the hidden pixel.
        int repairTop = Math.Max(viewportHeight - scrollDelta, scrollDelta);
        if (repairTop >= viewportHeight) return FixedOverlayRepair.Empty;

        int columns = (currentFrame.Width + TileWidth - 1) / TileWidth;
        int rows = (viewportHeight + TileHeight - 1) / TileHeight;
        bool[,] strong = new bool[rows, columns];

        for (int row = 0; row < rows; row++)
        {
            int y = row * TileHeight;
            if (y < repairTop || y - scrollDelta < 0) continue;
            int height = Math.Min(TileHeight, viewportHeight - y);
            if (y - scrollDelta + height > viewportHeight) continue;

            for (int column = 0; column < columns; column++)
            {
                int x = column * TileWidth;
                int width = Math.Min(TileWidth, currentFrame.Width - x);
                double stationaryError = MeanAbsoluteError(
                    result, x, previousViewportTop + y,
                    currentFrame, x, y,
                    width, height);
                double documentMotionError = MeanAbsoluteError(
                    result, x, previousViewportTop + y,
                    currentFrame, x, y - scrollDelta,
                    width, height);

                strong[row, column] =
                    stationaryError <= 34.0 &&
                    documentMotionError >= 30.0 &&
                    documentMotionError >= stationaryError * 1.35 + 7.0;
            }
        }

        // Dynamic text/counters can make a tile inside a fixed button fail the strict test even when
        // its surrounding shell is clearly stationary. Expand by one tile around strong detections.
        // Copying current(y-delta) into a neighboring normal document tile is geometrically safe: it
        // is the exact same logical document position under the trusted anchor delta.
        bool[,] selected = DilateOneTile(strong);

        int detected = 0;
        int repaired = 0;
        int maskWidth = Math.Max(1, (currentFrame.Width + EvidenceScale - 1) / EvidenceScale);
        int maskHeight = Math.Max(1, (viewportHeight + EvidenceScale - 1) / EvidenceScale);
        Bitmap? mask = null;
        Graphics? maskGraphics = null;

        try
        {
            for (int row = 0; row < rows; row++)
            {
                int y = row * TileHeight;
                for (int column = 0; column < columns; column++)
                {
                    if (!selected[row, column]) continue;
                    detected++;

                    int x = column * TileWidth;
                    int width = Math.Min(TileWidth, currentFrame.Width - x);
                    int height = Math.Min(TileHeight, viewportHeight - y);
                    int sourceY = y - scrollDelta;
                    if (width <= 0 || height <= 0 || sourceY < 0 || sourceY + height > viewportHeight) continue;
                    if (y < repairTop) continue;

                    destinationGraphics.DrawImage(
                        currentFrame,
                        new Rectangle(x, previousViewportTop + y, width, height),
                        new Rectangle(x, sourceY, width, height),
                        GraphicsUnit.Pixel);
                    repaired++;

                    if (mask is null)
                    {
                        mask = new Bitmap(maskWidth, maskHeight, PixelFormat.Format24bppRgb);
                        maskGraphics = Graphics.FromImage(mask);
                        maskGraphics.Clear(Color.Black);
                    }
                    maskGraphics!.FillRectangle(
                        Brushes.White,
                        x / EvidenceScale,
                        y / EvidenceScale,
                        Math.Max(1, (width + EvidenceScale - 1) / EvidenceScale),
                        Math.Max(1, (height + EvidenceScale - 1) / EvidenceScale));
                }
            }
        }
        finally
        {
            maskGraphics?.Dispose();
        }

        return new FixedOverlayRepair(detected, repaired, mask);
    }

    private static bool[,] DilateOneTile(bool[,] strong)
    {
        int rows = strong.GetLength(0);
        int columns = strong.GetLength(1);
        bool[,] selected = (bool[,])strong.Clone();
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                if (!strong[row, column]) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int y = row + dy;
                        int x = column + dx;
                        if (y >= 0 && y < rows && x >= 0 && x < columns) selected[y, x] = true;
                    }
                }
            }
        }
        return selected;
    }

    private static double MeanAbsoluteError(
        Bitmap a, int ax, int ay,
        Bitmap b, int bx, int by,
        int width, int height)
    {
        long total = 0;
        long samples = 0;
        for (int y = 0; y < height; y += SampleStep)
        {
            for (int x = 0; x < width; x += SampleStep)
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
            File.AppendAllText(
                Path.Combine(directory, "fixed-overlay-evidence.jsonl"),
                line + Environment.NewLine,
                new UTF8Encoding(false));

            if (mask is not null)
            {
                mask.Save(
                    Path.Combine(directory, $"fixed-overlay-mask-{telemetry.AppendCount:D3}.png"),
                    ImageFormat.Png);
            }
        }
        catch
        {
            // Evidence is advisory and must never invalidate a capture.
        }
    }

    private sealed record FixedOverlayRepair(int DetectedTiles, int RepairedTiles, Bitmap? MaskSmall)
    {
        public static FixedOverlayRepair Empty { get; } = new(0, 0, null);
    }
}
