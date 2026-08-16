#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModV019CompositorTelemetry(
    int AppendCount,
    int RejectedAppendCount,
    int StationaryRiskFrames,
    int TailRepairComponents,
    int TailRepairPixelsApprox,
    int LatestScrollDelta,
    double LatestStationaryRiskRatio,
    double MaxStationaryRiskRatio,
    bool CommittedBodyImmutable,
    bool ProvisionalTailRepairEnabled,
    int RingValidatedRepairs,
    int PersistentEdgeRepairs,
    int RejectedLargeComponents);

/// <summary>
/// v0.1.10 goal-loop hardening of the v0.1.9 trust-split compositor.
///
/// The committed body remains immutable. Only the immediately previous append tail may be repaired,
/// and only from the next raw frame after geometry has already been validated independently.
///
/// The important change is fixed/sticky evidence. The old source-stationary veto produced false
/// negatives on real Linux.do captures because legitimate underlying document pixels can be white or
/// otherwise unchanged at the same screen coordinate. Instead, a repair candidate must now be a
/// screen-coordinate component that persists in BOTH adjacent transition masks. It is then accepted
/// only when either the surrounding ring demonstrates document motion, or a small edge component has
/// strong persistent evidence. Large components are rejected outright. This keeps the repair local to
/// fixed/sticky controls while preserving the immutable-body safety rule.
/// </summary>
internal static class ShareXModTrustSplitCompositorV019
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

    public static ShareXModV019CompositorTelemetry SnapshotLiveTelemetry()
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
        private const double MaxRepairComponentAreaRatio = 0.05;
        private const double StrongPersistentAreaRatio = 0.035;

        private int appendCount;
        private int rejectedAppendCount;
        private int stationaryRiskFrames;
        private int tailRepairComponents;
        private int tailRepairPixelsApprox;
        private int latestScrollDelta;
        private double latestStationaryRiskRatio;
        private double maxStationaryRiskRatio;
        private int previousAppendDelta;
        private HashSet<int> previousStationaryTiles = new();
        private int ringValidatedRepairs;
        private int persistentEdgeRepairs;
        private int rejectedLargeComponents;

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

            int targetTailDelta = previousAppendDelta > 0 ? previousAppendDelta : scrollDelta;
            StationaryMap stationary = DetectStationaryTiles(previousRaw, currentRaw, scrollDelta, targetTailDelta);
            latestStationaryRiskRatio = stationary.RiskRatio;
            maxStationaryRiskRatio = Math.Max(maxStationaryRiskRatio, stationary.RiskRatio);
            if (stationary.RiskRatio >= 0.015) stationaryRiskFrames++;

            if (previousAppendDelta > 0 && previousStationaryTiles.Count > 0 && stationary.Tiles.Count > 0)
            {
                RepairProvisionalTail(
                    result,
                    previousRaw,
                    currentRaw,
                    scrollDelta,
                    previousAppendDelta,
                    stationary.Tiles,
                    previousStationaryTiles);
            }

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
            previousAppendDelta = scrollDelta;
            previousStationaryTiles = stationary.Tiles;
            TryWriteEvidence(currentRaw.Size, scrollDelta, stationary.RiskRatio);
            return combined;
        }

        public ShareXModV019CompositorTelemetry SnapshotTelemetry() => new(
            appendCount,
            rejectedAppendCount,
            stationaryRiskFrames,
            tailRepairComponents,
            tailRepairPixelsApprox,
            latestScrollDelta,
            latestStationaryRiskRatio,
            maxStationaryRiskRatio,
            true,
            true,
            ringValidatedRepairs,
            persistentEdgeRepairs,
            rejectedLargeComponents);

        private void RepairProvisionalTail(
            Bitmap result,
            Bitmap previous,
            Bitmap current,
            int currentDelta,
            int previousDelta,
            HashSet<int> currentTiles,
            HashSet<int> priorTiles)
        {
            int width = current.Width;
            int height = current.Height;
            int columns = (width + TileWidth - 1) / TileWidth;
            int rows = (height + TileHeight - 1) / TileHeight;
            int provisionalTop = height - previousDelta;
            int resultViewportTop = result.Height - height;

            // Only pixels that were independently stationary in BOTH adjacent transitions can
            // become repair evidence. This is much stricter than merely requiring a small overlap
            // ratio between two larger components.
            var persistentTiles = new HashSet<int>(currentTiles);
            persistentTiles.IntersectWith(priorTiles);

            foreach (List<int> component in ConnectedComponents(persistentTiles, columns, rows))
            {
                if (component.Count < 2) continue;

                int minXTile = int.MaxValue, maxXTile = -1, minYTile = int.MaxValue, maxYTile = -1;
                foreach (int key in component)
                {
                    int tileY = key / columns;
                    int tileX = key % columns;
                    minXTile = Math.Min(minXTile, tileX);
                    maxXTile = Math.Max(maxXTile, tileX);
                    minYTile = Math.Min(minYTile, tileY);
                    maxYTile = Math.Max(maxYTile, tileY);
                }

                int rawX0 = minXTile * TileWidth;
                int rawX1 = Math.Min(width, (maxXTile + 1) * TileWidth);
                int rawY0 = minYTile * TileHeight;
                int rawY1 = Math.Min(height, (maxYTile + 1) * TileHeight);

                bool rightEdge = rawX1 >= width * 0.72;
                bool bottomEdge = rawY1 >= height * 0.88;
                if (!rightEdge && !bottomEdge) continue;

                long rawArea = (long)(rawX1 - rawX0) * (rawY1 - rawY0);
                long viewportArea = (long)width * height;
                if (rawArea > viewportArea * MaxRepairComponentAreaRatio)
                {
                    rejectedLargeComponents++;
                    continue;
                }

                // Two tile margins recover antialiased borders/text around the stable interior of a
                // fixed control. The component itself already passed two-transition persistence.
                int marginX = TileWidth * 2;
                int marginY = TileHeight * 2;
                int x0 = Math.Max(0, rawX0 - marginX);
                int x1 = Math.Min(width, rawX1 + marginX);
                int y0 = Math.Max(provisionalTop, rawY0 - marginY);
                int y1 = Math.Min(height, rawY1 + marginY);

                int sourceY0 = y0 - currentDelta;
                int sourceY1 = y1 - currentDelta;
                if (sourceY0 < 0)
                {
                    y0 += -sourceY0;
                    sourceY0 = 0;
                }
                if (sourceY1 > height)
                {
                    int trim = sourceY1 - height;
                    y1 -= trim;
                    sourceY1 = height;
                }
                if (x1 <= x0 || y1 <= y0 || sourceY1 <= sourceY0) continue;

                bool ringValidated = HasSurroundingDocumentMotion(
                    previous, current, x0, y0, x1, y1, currentDelta);
                bool strongPersistentEdge = component.Count >= 4 && rawArea <= viewportArea * StrongPersistentAreaRatio;
                if (!ringValidated && !strongPersistentEdge) continue;

                int repairWidth = x1 - x0;
                int repairHeight = y1 - y0;
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.None;
                    graphics.DrawImage(
                        current,
                        new Rectangle(x0, resultViewportTop + y0, repairWidth, repairHeight),
                        new Rectangle(x0, sourceY0, repairWidth, repairHeight),
                        GraphicsUnit.Pixel);
                }

                tailRepairComponents++;
                tailRepairPixelsApprox += repairWidth * repairHeight;
                if (ringValidated) ringValidatedRepairs++;
                else persistentEdgeRepairs++;
            }
        }

        private static bool HasSurroundingDocumentMotion(
            Bitmap previous,
            Bitmap current,
            int x0,
            int y0,
            int x1,
            int y1,
            int delta)
        {
            int good = 0;
            int marginY = TileHeight;
            int marginX = TileWidth;

            // A fixed overlay hides the inner pixels in the previous frame, so validate geometry on
            // the immediately surrounding ring instead. At least one ring segment must agree with
            // document motion: previous(y) ~= current(y-delta), while same-screen coordinates differ.
            if (TryMotionSegment(previous, current, x0, y0 - marginY, x1, y0, delta)) good++;
            if (TryMotionSegment(previous, current, x0, y1, x1, y1 + marginY, delta)) good++;
            if (TryMotionSegment(previous, current, x0 - marginX, y0, x0, y1, delta)) good++;
            if (TryMotionSegment(previous, current, x1, y0, x1 + marginX, y1, delta)) good++;
            return good >= 1;
        }

        private static bool TryMotionSegment(
            Bitmap previous,
            Bitmap current,
            int x0,
            int y0,
            int x1,
            int y1,
            int delta)
        {
            x0 = Math.Max(0, x0);
            y0 = Math.Max(0, y0);
            x1 = Math.Min(previous.Width, x1);
            y1 = Math.Min(previous.Height, y1);
            if (x1 - x0 < 8 || y1 - y0 < 8) return false;
            if (y0 - delta < 0 || y1 - delta > current.Height) return false;

            int width = x1 - x0;
            int height = y1 - y0;
            double shifted = MeanAbsoluteError(previous, x0, y0, current, x0, y0 - delta, width, height);
            double same = MeanAbsoluteError(previous, x0, y0, current, x0, y0, width, height);
            return shifted <= 20.0 && same >= Math.Max(4.0, shifted * 1.15);
        }

        private static StationaryMap DetectStationaryTiles(
            Bitmap previous,
            Bitmap current,
            int delta,
            int targetTailDelta)
        {
            int width = current.Width;
            int height = current.Height;
            int columns = (width + TileWidth - 1) / TileWidth;
            int appendTop = Math.Max(0, height - Math.Max(1, targetTailDelta));
            var tiles = new HashSet<int>();
            long candidateArea = 0;
            long inspectedArea = 0;

            for (int y = 0; y < height; y += TileHeight)
            {
                int h = Math.Min(TileHeight, height - y);
                if (y + h <= appendTop) continue;
                int shiftedY = y - delta;
                if (shiftedY < 0 || shiftedY + h > height) continue;

                int tileY = y / TileHeight;
                for (int x = 0; x < width; x += TileWidth)
                {
                    int w = Math.Min(TileWidth, width - x);
                    bool rightOrBottom = x + w >= width * 0.72 || y + h >= height * 0.88;
                    if (!rightOrBottom) continue;

                    double same = MeanAbsoluteError(previous, x, y, current, x, y, w, h);
                    double shifted = MeanAbsoluteError(previous, x, y, current, x, shiftedY, w, h);
                    long area = (long)w * h;
                    inspectedArea += area;

                    if (same <= 20.0 && shifted >= 26.0 && shifted >= same * 1.35 + 6.0)
                    {
                        int tileX = x / TileWidth;
                        tiles.Add(tileY * columns + tileX);
                        candidateArea += area;
                    }
                }
            }

            return new StationaryMap(
                tiles,
                inspectedArea <= 0 ? 0 : candidateArea / (double)inspectedArea);
        }

        private static IEnumerable<List<int>> ConnectedComponents(HashSet<int> tiles, int columns, int rows)
        {
            var seen = new HashSet<int>();
            foreach (int start in tiles)
            {
                if (!seen.Add(start)) continue;
                var component = new List<int>();
                var stack = new Stack<int>();
                stack.Push(start);

                while (stack.Count > 0)
                {
                    int key = stack.Pop();
                    component.Add(key);
                    int y = key / columns;
                    int x = key % columns;
                    int[] neighbours =
                    {
                        (y - 1) * columns + x,
                        (y + 1) * columns + x,
                        y * columns + (x - 1),
                        y * columns + (x + 1)
                    };
                    for (int i = 0; i < neighbours.Length; i++)
                    {
                        int neighbour = neighbours[i];
                        int ny = neighbour / columns;
                        int nx = neighbour % columns;
                        bool valid = i switch
                        {
                            0 => y > 0,
                            1 => y + 1 < rows,
                            2 => x > 0,
                            3 => x + 1 < columns,
                            _ => false
                        };
                        if (!valid) continue;
                        if (ny < 0 || ny >= rows || nx < 0 || nx >= columns) continue;
                        if (tiles.Contains(neighbour) && seen.Add(neighbour)) stack.Push(neighbour);
                    }
                }

                yield return component;
            }
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
            return samples == 0 ? 0 : total / (double)samples;
        }

        private void TryWriteEvidence(Size viewport, int delta, double stationaryRisk)
        {
            try
            {
                string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
                if (string.IsNullOrWhiteSpace(root)) return;
                string directory = Path.Combine(root, "trust-split-v019");
                Directory.CreateDirectory(directory);
                ShareXModCaptureSessionContext.RegisterComponent("trust-split-v019", directory);
                File.AppendAllText(
                    Path.Combine(directory, "compositor.jsonl"),
                    JsonSerializer.Serialize(new
                    {
                        timestamp = DateTimeOffset.Now,
                        viewport = new { viewport.Width, viewport.Height },
                        delta,
                        stationaryRisk,
                        policy = "committed-body-immutable-persistent-edge-provisional-tail-repair-v020",
                        telemetry = SnapshotTelemetry()
                    }) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        public void Dispose()
        {
            previousStationaryTiles.Clear();
            previousAppendDelta = 0;
        }

        private sealed record StationaryMap(HashSet<int> Tiles, double RiskRatio);
    }
}
