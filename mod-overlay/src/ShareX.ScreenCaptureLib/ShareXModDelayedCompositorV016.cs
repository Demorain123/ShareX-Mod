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

internal readonly record struct ShareXModV016Telemetry(
    int AppendCount,
    int DetectedComponents,
    int RepairedComponents,
    int RepairedPixelsApprox,
    int PendingTailComponents,
    int LatestScrollDelta,
    bool LowOverlapRisk);

/// <summary>
/// v0.1.6 compositor: fixed/sticky detection is performed only against unmodified raw neighbouring
/// frames. The already-composited mosaic is an output surface, never the evidence source.
///
/// Strong stationary tiles seed a component. Looser same-screen tiles may join that component so
/// dynamic text/counters inside a fixed button do not leave holes. Only edge/bottom components that
/// intersect the append band are eligible, which avoids treating temporarily static body text as UI.
/// </summary>
internal static class ShareXModDelayedCompositorV016
{
    private static readonly object LiveSync = new();
    private static Session? liveSession;

    public static Bitmap? TryAppendLive(Bitmap result, Bitmap previousRaw, Bitmap currentRaw, int scrollDelta)
    {
        lock (LiveSync)
        {
            if (liveSession is null || result.Height <= currentRaw.Height)
            {
                liveSession?.Dispose();
                liveSession = new Session();
            }
            return liveSession.TryAppend(result, previousRaw, currentRaw, scrollDelta);
        }
    }

    public static ShareXModV016Telemetry SnapshotLiveTelemetry()
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
        private const int TileWidth = 32;
        private const int TileHeight = 24;
        private const int SampleStep = 4;
        private const int EvidenceScale = 4;

        private readonly List<TrackedComponent> tracks = new();
        private int appendCount;
        private int detectedComponents;
        private int repairedComponents;
        private int repairedPixelsApprox;
        private int pendingTailComponents;
        private int latestScrollDelta;
        private bool lowOverlapRisk;

        public Bitmap? TryAppend(Bitmap result, Bitmap previousRaw, Bitmap currentRaw, int scrollDelta)
        {
            if (result is null || previousRaw is null || currentRaw is null) return null;
            if (previousRaw.Size != currentRaw.Size || result.Width != currentRaw.Width) return null;
            if (scrollDelta <= 0 || scrollDelta >= currentRaw.Height) return null;

            int viewportHeight = currentRaw.Height;
            int previousViewportTop = result.Height - viewportHeight;
            if (previousViewportTop < 0) return null;

            List<Component> components = DetectComponents(previousRaw, currentRaw, scrollDelta);
            List<Component> selected = UpdateTracks(components, currentRaw.Size);

            Bitmap combined = new(result.Width, checked(result.Height + scrollDelta), PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(combined);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.DrawImageUnscaled(result, 0, 0);

            int repairTop = Math.Max(viewportHeight - scrollDelta, scrollDelta);
            int repairedThisFrame = 0;
            int repairedPixelsThisFrame = 0;

            foreach (Component component in selected)
            {
                Rectangle repair = Rectangle.Intersect(
                    component.Bounds,
                    new Rectangle(0, repairTop, currentRaw.Width, viewportHeight - repairTop));
                if (repair.IsEmpty) continue;

                int sourceY = repair.Y - scrollDelta;
                if (sourceY < 0 || sourceY + repair.Height > viewportHeight) continue;

                graphics.DrawImage(
                    currentRaw,
                    new Rectangle(repair.X, previousViewportTop + repair.Y, repair.Width, repair.Height),
                    new Rectangle(repair.X, sourceY, repair.Width, repair.Height),
                    GraphicsUnit.Pixel);
                repairedThisFrame++;
                repairedPixelsThisFrame += repair.Width * repair.Height;
            }

            // Append only the new document band. Any fixed component in this newest tail is pending;
            // the next raw frame provides the real pixels behind it. This guarantees at most one tail
            // occurrence when the user stops manually, rather than one occurrence per scroll step.
            graphics.DrawImage(
                currentRaw,
                new Rectangle(0, result.Height, currentRaw.Width, scrollDelta),
                new Rectangle(0, viewportHeight - scrollDelta, currentRaw.Width, scrollDelta),
                GraphicsUnit.Pixel);

            appendCount++;
            detectedComponents += components.Count;
            repairedComponents += repairedThisFrame;
            repairedPixelsApprox += repairedPixelsThisFrame;
            pendingTailComponents = selected.Count(x => x.Bounds.Bottom > viewportHeight - scrollDelta);
            latestScrollDelta = scrollDelta;
            lowOverlapRisk = scrollDelta > viewportHeight / 2;

            TryWriteEvidence(previousRaw.Size, components, selected, repairTop);
            return combined;
        }

        public ShareXModV016Telemetry SnapshotTelemetry() => new(
            appendCount,
            detectedComponents,
            repairedComponents,
            repairedPixelsApprox,
            pendingTailComponents,
            latestScrollDelta,
            lowOverlapRisk);

        private List<Component> DetectComponents(Bitmap previous, Bitmap current, int delta)
        {
            int width = current.Width;
            int height = current.Height;
            int repairTop = Math.Max(height - delta, delta);
            int columns = (width + TileWidth - 1) / TileWidth;
            int rows = (height + TileHeight - 1) / TileHeight;
            bool[,] strong = new bool[rows, columns];
            bool[,] candidate = new bool[rows, columns];

            for (int row = 0; row < rows; row++)
            {
                int y = row * TileHeight;
                if (y + TileHeight < repairTop || y - delta < 0) continue;
                int tileHeight = Math.Min(TileHeight, height - y);
                if (y - delta + tileHeight > height) continue;

                for (int column = 0; column < columns; column++)
                {
                    int x = column * TileWidth;
                    int tileWidth = Math.Min(TileWidth, width - x);
                    bool edgeOrBottom =
                        x <= width * 0.24 || x + tileWidth >= width * 0.76 ||
                        y + tileHeight >= height * 0.90;
                    if (!edgeOrBottom) continue;

                    double stationary = MeanAbsoluteError(previous, x, y, current, x, y, tileWidth, tileHeight);
                    double motion = MeanAbsoluteError(previous, x, y, current, x, y - delta, tileWidth, tileHeight);

                    bool strongTile =
                        stationary <= 40.0 &&
                        motion >= 28.0 &&
                        motion >= stationary * 1.28 + 6.0;
                    bool looseTile =
                        stationary <= 72.0 &&
                        motion >= 22.0 &&
                        motion >= stationary * 1.06 + 4.0;

                    strong[row, column] = strongTile;
                    candidate[row, column] = looseTile || strongTile;
                }
            }

            bool[,] visited = new bool[rows, columns];
            var components = new List<Component>();

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    if (!strong[row, column] || visited[row, column]) continue;

                    var queue = new Queue<(int Row, int Column)>();
                    queue.Enqueue((row, column));
                    visited[row, column] = true;
                    int minRow = row, maxRow = row, minColumn = column, maxColumn = column;
                    int strongCount = 0, tileCount = 0;

                    while (queue.Count > 0)
                    {
                        (int r, int c) = queue.Dequeue();
                        tileCount++;
                        if (strong[r, c]) strongCount++;
                        minRow = Math.Min(minRow, r);
                        maxRow = Math.Max(maxRow, r);
                        minColumn = Math.Min(minColumn, c);
                        maxColumn = Math.Max(maxColumn, c);

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nr = r + dy;
                                int nc = c + dx;
                                if (nr < 0 || nr >= rows || nc < 0 || nc >= columns || visited[nr, nc] || !candidate[nr, nc]) continue;
                                visited[nr, nc] = true;
                                queue.Enqueue((nr, nc));
                            }
                        }
                    }

                    if (strongCount <= 0) continue;
                    Rectangle bounds = Rectangle.FromLTRB(
                        Math.Max(0, (minColumn - 1) * TileWidth),
                        Math.Max(repairTop, (minRow - 1) * TileHeight),
                        Math.Min(width, (maxColumn + 2) * TileWidth),
                        Math.Min(height, (maxRow + 2) * TileHeight));
                    if (bounds.IsEmpty) continue;

                    double areaRatio = bounds.Width * bounds.Height / (double)(width * height);
                    if (areaRatio > 0.35 && bounds.Width < width * 0.65) continue;
                    if (strongCount == 1 && tileCount == 1 && bounds.Width * bounds.Height < 2200) continue;

                    components.Add(new Component(bounds, strongCount, tileCount));
                }
            }

            return MergeOverlapping(components);
        }

        private List<Component> UpdateTracks(List<Component> current, Size viewport)
        {
            foreach (TrackedComponent track in tracks) track.SeenThisFrame = false;
            var selected = new List<Component>();

            foreach (Component component in current)
            {
                TrackedComponent? match = tracks
                    .Where(x => RectanglesNear(x.Bounds, component.Bounds))
                    .OrderByDescending(x => IntersectionOverUnion(x.Bounds, component.Bounds))
                    .FirstOrDefault();

                if (match is null)
                {
                    match = new TrackedComponent(component.Bounds, 1);
                    tracks.Add(match);
                }
                else
                {
                    match.Bounds = Rectangle.Union(match.Bounds, component.Bounds);
                    match.Streak++;
                }
                match.SeenThisFrame = true;

                // A component with multiple strong seeds is safe immediately. A weaker component
                // must persist at the same screen coordinates for a second neighbouring-frame pair.
                if (component.StrongTiles >= 2 || match.Streak >= 2)
                {
                    Rectangle bounded = Rectangle.Intersect(match.Bounds, new Rectangle(Point.Empty, viewport));
                    if (!bounded.IsEmpty) selected.Add(component with { Bounds = bounded });
                }
            }

            for (int i = tracks.Count - 1; i >= 0; i--)
            {
                if (!tracks[i].SeenThisFrame)
                {
                    tracks[i].Misses++;
                    tracks[i].Streak = Math.Max(0, tracks[i].Streak - 1);
                    if (tracks[i].Misses >= 3) tracks.RemoveAt(i);
                }
                else
                {
                    tracks[i].Misses = 0;
                }
            }

            return MergeOverlapping(selected);
        }

        private static List<Component> MergeOverlapping(List<Component> source)
        {
            var result = new List<Component>();
            foreach (Component item in source.OrderByDescending(x => x.Bounds.Width * x.Bounds.Height))
            {
                int index = result.FindIndex(x => RectanglesNear(x.Bounds, item.Bounds));
                if (index < 0)
                {
                    result.Add(item);
                }
                else
                {
                    Component existing = result[index];
                    result[index] = new Component(
                        Rectangle.Union(existing.Bounds, item.Bounds),
                        existing.StrongTiles + item.StrongTiles,
                        existing.TotalTiles + item.TotalTiles);
                }
            }
            return result;
        }

        private void TryWriteEvidence(Size viewport, List<Component> detected, List<Component> selected, int repairTop)
        {
            try
            {
                string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
                if (string.IsNullOrWhiteSpace(root)) return;
                string directory = Path.Combine(root, "delayed-compositor-v016");
                Directory.CreateDirectory(directory);
                ShareXModCaptureSessionContext.RegisterComponent("delayed-compositor-v016", directory);

                File.AppendAllText(
                    Path.Combine(directory, "components.jsonl"),
                    JsonSerializer.Serialize(new
                    {
                        append = appendCount,
                        timestamp = DateTimeOffset.Now,
                        viewport = new { viewport.Width, viewport.Height },
                        latestScrollDelta,
                        repairTop,
                        detected = detected.Select(x => new { x.Bounds, x.StrongTiles, x.TotalTiles }).ToArray(),
                        selected = selected.Select(x => new { x.Bounds, x.StrongTiles, x.TotalTiles }).ToArray(),
                        telemetry = SnapshotTelemetry()
                    }) + Environment.NewLine,
                    new UTF8Encoding(false));

                if (selected.Count > 0)
                {
                    int maskWidth = Math.Max(1, (viewport.Width + EvidenceScale - 1) / EvidenceScale);
                    int maskHeight = Math.Max(1, (viewport.Height + EvidenceScale - 1) / EvidenceScale);
                    using Bitmap mask = new(maskWidth, maskHeight, PixelFormat.Format24bppRgb);
                    using Graphics g = Graphics.FromImage(mask);
                    g.Clear(Color.Black);
                    foreach (Component component in selected)
                    {
                        Rectangle r = component.Bounds;
                        g.FillRectangle(
                            Brushes.White,
                            r.X / EvidenceScale,
                            r.Y / EvidenceScale,
                            Math.Max(1, (r.Width + EvidenceScale - 1) / EvidenceScale),
                            Math.Max(1, (r.Height + EvidenceScale - 1) / EvidenceScale));
                    }
                    mask.Save(Path.Combine(directory, $"component-mask-{appendCount:D3}.png"), ImageFormat.Png);
                }
            }
            catch
            {
            }
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

        private static bool RectanglesNear(Rectangle a, Rectangle b)
        {
            if (a.IntersectsWith(b)) return true;
            int ax = a.Left + a.Width / 2;
            int ay = a.Top + a.Height / 2;
            int bx = b.Left + b.Width / 2;
            int by = b.Top + b.Height / 2;
            return Math.Abs(ax - bx) <= 56 && Math.Abs(ay - by) <= 48;
        }

        private static double IntersectionOverUnion(Rectangle a, Rectangle b)
        {
            Rectangle intersection = Rectangle.Intersect(a, b);
            if (intersection.IsEmpty) return 0;
            double intersectionArea = intersection.Width * intersection.Height;
            double union = a.Width * a.Height + b.Width * b.Height - intersectionArea;
            return union <= 0 ? 0 : intersectionArea / union;
        }

        public void Dispose()
        {
            tracks.Clear();
        }

        private readonly record struct Component(Rectangle Bounds, int StrongTiles, int TotalTiles);

        private sealed class TrackedComponent
        {
            public Rectangle Bounds;
            public int Streak;
            public int Misses;
            public bool SeenThisFrame;

            public TrackedComponent(Rectangle bounds, int streak)
            {
                Bounds = bounds;
                Streak = streak;
            }
        }
    }
}
