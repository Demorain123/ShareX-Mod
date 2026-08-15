#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModV017AnchorTelemetry(
    int DirectAnchors,
    int FallbackAnchors,
    int PriorValidatedAnchors,
    int UnresolvedTransitions,
    int LatestDelta,
    string LatestSource,
    double LatestScore);

/// <summary>
/// Keeps every accepted transition on the same raw-frame delayed-compositor path.
/// A direct multi-anchor result is preferred. If that deliberately conservative matcher
/// rejects a difficult frame, a central-body vertical matcher is used. A stable recent
/// scroll-delta prior may be used only after that exact delta is independently validated
/// against the current raw-frame pair. There is deliberately no legacy mosaic matcher fallback.
/// </summary>
internal static class ShareXModAnchorContinuityV017
{
    private static readonly object Sync = new();
    private static readonly Queue<int> RecentReliableDeltas = new();
    private static int directAnchors;
    private static int fallbackAnchors;
    private static int priorValidatedAnchors;
    private static int unresolvedTransitions;
    private static int latestDelta;
    private static string latestSource = "none";
    private static double latestScore = -1;

    public static bool TryResolve(
        Bitmap previous,
        Bitmap current,
        bool hasDirectAnchor,
        ShareXModAnchorMatch directAnchor,
        out int delta,
        out string source,
        out double score)
    {
        delta = 0;
        source = "unresolved";
        score = double.MaxValue;

        lock (Sync)
        {
            if (hasDirectAnchor && directAnchor.ScrollDelta > 0 && directAnchor.ScrollDelta < current.Height)
            {
                delta = directAnchor.ScrollDelta;
                score = directAnchor.Score;
                source = "direct-anchor";
                directAnchors++;
                Remember(delta);
                UpdateLatest(delta, source, score);
                WriteEvidence(delta, source, score, true);
                return true;
            }

            ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();
            if (ShareXModVerticalFallbackMatcher.TryEstimateScrollDeltaOnly(
                    previous, current, settings, out int fallbackDelta, out double fallbackScore))
            {
                delta = fallbackDelta;
                score = fallbackScore;
                source = "vertical-fallback";
                fallbackAnchors++;
                Remember(delta);
                UpdateLatest(delta, source, score);
                WriteEvidence(delta, source, score, true);
                return true;
            }

            if (TryGetStablePrior(out int prior) &&
                ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                    previous, current, settings, prior, out double priorScore))
            {
                delta = prior;
                score = priorScore;
                source = "validated-prior";
                priorValidatedAnchors++;
                Remember(delta);
                UpdateLatest(delta, source, score);
                WriteEvidence(delta, source, score, true);
                return true;
            }

            unresolvedTransitions++;
            UpdateLatest(0, source, score);
            WriteEvidence(0, source, score, false);
            return false;
        }
    }

    public static ShareXModV017AnchorTelemetry SnapshotTelemetry()
    {
        lock (Sync)
        {
            return new ShareXModV017AnchorTelemetry(
                directAnchors,
                fallbackAnchors,
                priorValidatedAnchors,
                unresolvedTransitions,
                latestDelta,
                latestSource,
                latestScore);
        }
    }

    public static void ResetLive()
    {
        lock (Sync)
        {
            RecentReliableDeltas.Clear();
            directAnchors = 0;
            fallbackAnchors = 0;
            priorValidatedAnchors = 0;
            unresolvedTransitions = 0;
            latestDelta = 0;
            latestSource = "none";
            latestScore = -1;
        }
    }

    private static void Remember(int delta)
    {
        RecentReliableDeltas.Enqueue(delta);
        while (RecentReliableDeltas.Count > 5) RecentReliableDeltas.Dequeue();
    }

    private static bool TryGetStablePrior(out int prior)
    {
        prior = 0;
        if (RecentReliableDeltas.Count < 2) return false;
        int[] values = RecentReliableDeltas.OrderBy(x => x).ToArray();
        int median = values[values.Length / 2];
        int maxDeviation = values.Max(x => Math.Abs(x - median));
        if (maxDeviation > Math.Max(32, median / 10)) return false;
        prior = median;
        return prior > 0;
    }

    private static void UpdateLatest(int delta, string source, double score)
    {
        latestDelta = delta;
        latestSource = source;
        latestScore = double.IsFinite(score) ? score : -1;
    }

    private static void WriteEvidence(int delta, string source, double score, bool resolved)
    {
        try
        {
            string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
            if (string.IsNullOrWhiteSpace(root)) return;
            string directory = Path.Combine(root, "anchor-continuity-v017");
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("anchor-continuity-v017", directory);
            File.AppendAllText(
                Path.Combine(directory, "resolutions.jsonl"),
                JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.Now,
                    resolved,
                    delta,
                    source,
                    score = double.IsFinite(score) ? score : -1,
                    telemetry = SnapshotTelemetry()
                }) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}
