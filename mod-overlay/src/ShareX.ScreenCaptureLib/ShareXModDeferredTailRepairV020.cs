#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModDeferredTailRepairTelemetry(
    int Queued,
    int Completed,
    int Pending,
    int Expired,
    int Retried,
    int CopiedPixels);

/// <summary>
/// Holds fixed/sticky repair rectangles that are not fully revealed by the immediately following raw
/// frame. Each job also retains the two-transition fixed mask that created it. Future retries veto
/// source pixels against that historical confirmed mask UNION the current persistent mask, so dynamic
/// text/color changes inside a fixed control cannot make the safety condition forget that location.
/// </summary>
internal sealed class ShareXModDeferredTailRepairV020
{
    private const int MaxPendingJobs = 24;
    private const int MaxAgeTransitions = 6;

    private readonly List<Job> jobs = new();
    private int queued;
    private int completed;
    private int expired;
    private int retried;
    private int copiedPixels;

    internal void Enqueue(
        int resultViewportTop,
        int x0,
        int y0,
        int x1,
        int y1,
        int initialCumulativeDelta,
        int requiredPixels,
        HashSet<int> confirmedFixedTiles)
    {
        if (x1 <= x0 || y1 <= y0 || initialCumulativeDelta <= 0 || requiredPixels <= 0) return;

        for (int i = 0; i < jobs.Count; i++)
        {
            Job existing = jobs[i];
            if (existing.ResultViewportTop == resultViewportTop &&
                RectanglesOverlap(existing.X0, existing.Y0, existing.X1, existing.Y1, x0, y0, x1, y1))
            {
                existing.X0 = Math.Min(existing.X0, x0);
                existing.Y0 = Math.Min(existing.Y0, y0);
                existing.X1 = Math.Max(existing.X1, x1);
                existing.Y1 = Math.Max(existing.Y1, y1);
                existing.CumulativeDelta = Math.Max(existing.CumulativeDelta, initialCumulativeDelta);
                existing.RequiredPixels = Math.Max(existing.RequiredPixels, requiredPixels);
                existing.ConfirmedFixedTiles.UnionWith(confirmedFixedTiles);
                return;
            }
        }

        if (jobs.Count >= MaxPendingJobs)
        {
            jobs.RemoveAt(0);
            expired++;
        }
        jobs.Add(new Job(
            resultViewportTop,
            x0,
            y0,
            x1,
            y1,
            initialCumulativeDelta,
            requiredPixels,
            confirmedFixedTiles));
        queued++;
    }

    internal void AdvanceAndRepair(
        Bitmap result,
        Bitmap currentRaw,
        int acceptedDelta,
        HashSet<int> currentPersistentFixedTiles,
        int columns,
        int tileWidth,
        int tileHeight)
    {
        if (jobs.Count == 0 || acceptedDelta <= 0) return;

        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            Job job = jobs[i];
            job.CumulativeDelta = checked(job.CumulativeDelta + acceptedDelta);
            job.AgeTransitions++;
            retried++;

            var sourceVetoTiles = new HashSet<int>(job.ConfirmedFixedTiles);
            sourceVetoTiles.UnionWith(currentPersistentFixedTiles);
            ShareXModSafeTailCopyResult copy = ShareXModSafeTailCopyV020.CopyDetailed(
                result,
                currentRaw,
                sourceVetoTiles,
                columns,
                job.ResultViewportTop,
                job.X0,
                job.Y0,
                job.X1,
                job.Y1,
                job.CumulativeDelta,
                tileWidth,
                tileHeight);
            copiedPixels += copy.CopiedPixels;

            if (copy.Complete)
            {
                jobs.RemoveAt(i);
                completed++;
                continue;
            }

            if (job.CumulativeDelta >= job.Y1 || job.AgeTransitions >= MaxAgeTransitions)
            {
                jobs.RemoveAt(i);
                expired++;
            }
        }
    }

    internal ShareXModDeferredTailRepairTelemetry Snapshot() =>
        new(queued, completed, jobs.Count, expired, retried, copiedPixels);

    internal void Clear() => jobs.Clear();

    private static bool RectanglesOverlap(
        int ax0, int ay0, int ax1, int ay1,
        int bx0, int by0, int bx1, int by1) =>
        ax0 < bx1 && bx0 < ax1 && ay0 < by1 && by0 < ay1;

    private sealed class Job
    {
        internal Job(
            int resultViewportTop,
            int x0,
            int y0,
            int x1,
            int y1,
            int cumulativeDelta,
            int requiredPixels,
            HashSet<int> confirmedFixedTiles)
        {
            ResultViewportTop = resultViewportTop;
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            CumulativeDelta = cumulativeDelta;
            RequiredPixels = requiredPixels;
            ConfirmedFixedTiles = new HashSet<int>(confirmedFixedTiles);
        }

        internal int ResultViewportTop { get; }
        internal int X0 { get; set; }
        internal int Y0 { get; set; }
        internal int X1 { get; set; }
        internal int Y1 { get; set; }
        internal int CumulativeDelta { get; set; }
        internal int RequiredPixels { get; set; }
        internal int AgeTransitions { get; set; }
        internal HashSet<int> ConfirmedFixedTiles { get; }
    }
}
