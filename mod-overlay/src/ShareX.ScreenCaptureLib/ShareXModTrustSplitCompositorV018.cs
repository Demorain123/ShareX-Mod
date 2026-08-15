#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModV018CompositorTelemetry(
    int AppendCount,
    int RejectedAppendCount,
    int StationaryRiskFrames,
    int LatestScrollDelta,
    double LatestStationaryRiskRatio,
    double MaxStationaryRiskRatio,
    bool PixelOverlayRepairEnabled);

/// <summary>
/// v0.1.8 trust-split raster compositor.
///
/// Motion evidence and overlay handling are deliberately separated. Once a transition delta has
/// been validated, this compositor performs exactly one operation: append the newly exposed strip.
/// It never classifies a large raster component as fixed/sticky and never rewrites already accepted
/// document pixels. Same-screen stationary evidence is measured only as a quality/diagnostic signal.
///
/// This is intentionally conservative. Browser-aware providers can remove fixed/sticky DOM nodes
/// using semantic information; the generic raster path must prefer a visible repeated overlay over
/// silently destroying real page/application content.
/// </summary>
internal static class ShareXModTrustSplitCompositorV018
{
    private static readonly object LiveSync = new();
    private static Session? liveSession;

    public static Bitmap? TryAppendLive(Bitmap result, Bitmap previousRaw, Bitmap currentRaw, int scrollDelta)
    {
        lock (LiveSync)
        {
            liveSession ??= new Session();
            return liveSession.TryAppend(result, previousRaw, currentRaw, scrollDelta);
        }
    }

    public static ShareXModV018CompositorTelemetry SnapshotLiveTelemetry()
    {
        lock (LiveSync) return liveSession?.SnapshotTelemetry() ?? default;
    }

    public static void ResetLive()
    {
        lock (LiveSync)
        {
            liveSession?.Dispose();
            liveSession = null;
        }
    }

    internal sealed class Session : IDisposable
    {
        private const int TileWidth = 48;
        private const int TileHeight = 36;
        private const int SampleStep = 6;

        private int appendCount;
        private int rejectedAppendCount;
        private int stationaryRiskFrames;
        private int latestScrollDelta;
        private double latestStationaryRiskRatio;
        private double maxStationaryRiskRatio;

        public Bitmap? TryAppend(Bitmap result, Bitmap previousRaw, Bitmap currentRaw, int scrollDelta)
        {
            if (result is null || previousRaw is null || currentRaw is null ||
                previousRaw.Size != currentRaw.Size || result.Width != currentRaw.Width ||
                scrollDelta <= 0 || scrollDelta >= currentRaw.Height)
            {
                rejectedAppendCount++;
                return null;
            }

            int viewportHeight = currentRaw.Height;
            int priorViewportTop = result.Height - viewportHeight;
            if (priorViewportTop < 0)
            {
                rejectedAppendCount++;
                return null;
            }

            double stationaryRisk = EstimateStationaryRisk(previousRaw, currentRaw, scrollDelta);
            latestStationaryRiskRatio = stationaryRisk;
            maxStationaryRiskRatio = Math.Max(maxStationaryRiskRatio, stationaryRisk);
            if (stationaryRisk >= 0.015) stationaryRiskFrames++;

            Bitmap combined = new(result.Width, checked(result.Height + scrollDelta), PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(combined))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.DrawImageUnscaled(result, 0, 0);
                graphics.DrawImage(
                    currentRaw,
                    new Rectangle(0, result.Height, currentRaw.Width, scrollDelta),
                    new Rectangle(0, viewportHeight - scrollDelta, currentRaw.Width, scrollDelta),
                    GraphicsUnit.Pixel);
            }

            appendCount++;
            latestScrollDelta = scrollDelta;
            TryWriteEvidence(currentRaw.Size, scrollDelta, stationaryRisk);
            return combined;
        }

        public ShareXModV018CompositorTelemetry SnapshotTelemetry() => new(
            appendCount,
            rejectedAppendCount,
            stationaryRiskFrames,
            latestScrollDelta,
            latestStationaryRiskRatio,
            maxStationaryRiskRatio,
            false);

        private static double EstimateStationaryRisk(Bitmap previous, Bitmap current, int delta)
        {
            int width = current.Width;
            int height = current.Height;
            int appendTop = height - delta;
            long candidateArea = 0;
            long inspectedArea = 0;

            for (int y = appendTop; y < height; y += TileHeight)
            {
                int h = Math.Min(TileHeight, height - y);
                if (y - delta < 0 || y - delta + h > height) continue;

                for (int x = 0; x < width; x += TileWidth)
                {
                    int w = Math.Min(TileWidth, width - x);
                    bool edgeOrBottom =
                        x <= width * 0.28 || x + w >= width * 0.72 || y + h >= height * 0.88;
                    if (!edgeOrBottom) continue;

                    double same = MeanAbsoluteError(previous, x, y, current, x, y, w, h);
                    double shifted = MeanAbsoluteError(previous, x, y, current, x, y - delta, w, h);
                    long area = (long)w * h;
                    inspectedArea += area;

                    // Diagnostic only. Never use this to alter pixels.
                    if (same <= 18.0 && shifted >= 30.0 && shifted >= same * 1.55 + 8.0)
                        candidateArea += area;
                }
            }

            return inspectedArea <= 0 ? 0 : candidateArea / (double)inspectedArea;
        }

        private static double MeanAbsoluteError(Bitmap a, int ax, int ay, Bitmap b, int bx, int by, int width, int height)
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
            return samples == 0 ? 0 : total / (double)samples;
        }

        private void TryWriteEvidence(Size viewport, int delta, double stationaryRisk)
        {
            try
            {
                string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
                if (string.IsNullOrWhiteSpace(root)) return;
                string directory = Path.Combine(root, "trust-split-v018");
                Directory.CreateDirectory(directory);
                ShareXModCaptureSessionContext.RegisterComponent("trust-split-v018", directory);
                File.AppendAllText(
                    Path.Combine(directory, "compositor.jsonl"),
                    JsonSerializer.Serialize(new
                    {
                        timestamp = DateTimeOffset.Now,
                        viewport = new { viewport.Width, viewport.Height },
                        delta,
                        stationaryRisk,
                        policy = "append-only-no-pixel-overlay-repair",
                        telemetry = SnapshotTelemetry()
                    }) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        public void Dispose() { }
    }
}
