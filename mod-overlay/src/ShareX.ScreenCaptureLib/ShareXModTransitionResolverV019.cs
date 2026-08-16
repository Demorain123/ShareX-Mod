#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModV019TransitionTelemetry(
    int DirectAccepted,
    int PriorResolved,
    int OutlierDirectRejected,
    int FullRangeRecovered,
    int RetryHeld,
    int RetryResolved,
    int TerminalUnresolved,
    int LatestDelta,
    string LatestSource,
    bool PendingRetry);

/// <summary>
/// v0.1.9 RC1 geometry resolver derived from the real v0.1.8 Linux.do evidence. Two independent
/// defects were visible in the recorded raw frames: low-consensus direct anchors could jump to a
/// visually similar short offset, while a legitimate one-off short wheel movement was rejected
/// because the temporal prior was treated as the only admissible search neighbourhood.
///
/// Every accepted delta now has to survive raw-pair validation. A stable prior may override a
/// conflicting direct anchor, an outlier direct anchor needs stronger consensus, and a prior miss
/// compares near-prior and full-range raw-pixel hypotheses instead of allowing a plausible repeated
/// pattern close to the prior to mask a much better global match. RC1 remains fail-closed if all
/// evidence fails; it does not use the old mosaic matcher or unsafe multi-frame catch-up.
/// </summary>
internal static class ShareXModTransitionResolverV019
{
    private static readonly object Sync = new();
    private static readonly Queue<int> RecentAcceptedDeltas = new();

    private static int directAccepted;
    private static int priorResolved;
    private static int outlierDirectRejected;
    private static int fullRangeRecovered;
    private static int terminalUnresolved;
    private static int latestDelta;
    private static string latestSource = "none";

    public static bool TryResolve(
        Bitmap previousReliable,
        Bitmap current,
        bool hasDirectAnchor,
        ShareXModAnchorMatch directAnchor,
        out int delta,
        out string source,
        out double score,
        out bool holdReliableReference)
    {
        lock (Sync)
        {
            delta = 0;
            source = "unresolved";
            score = double.MaxValue;
            holdReliableReference = false;

            ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();
            bool hasPrior = TryGetStablePrior(out int prior);

            if (hasDirectAnchor && directAnchor.ScrollDelta > 0 && directAnchor.ScrollDelta < current.Height)
            {
                if (!hasPrior)
                {
                    if (directAnchor.AgreementCount >= 2 &&
                        ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                            previousReliable, current, settings, directAnchor.ScrollDelta, out double directScore))
                    {
                        delta = directAnchor.ScrollDelta;
                        score = directScore;
                        source = "validated-direct-bootstrap";
                        directAccepted++;
                        Remember(delta);
                        Complete(delta, source, true);
                        return true;
                    }
                }
                else
                {
                    int tolerance = Math.Max(48, prior / 8);
                    bool nearPrior = Math.Abs(directAnchor.ScrollDelta - prior) <= tolerance;

                    if (nearPrior && directAnchor.AgreementCount >= 2)
                    {
                        if (ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                                previousReliable, current, settings, directAnchor.ScrollDelta, out double directScore))
                        {
                            delta = directAnchor.ScrollDelta;
                            score = directScore;
                            source = "validated-direct-near-prior";
                            directAccepted++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }
                    }
                    else
                    {
                        // The user's v0.1.8 run contained 200/404px direct anchors among a strong
                        // ~750px history. A conflicting direct anchor no longer bypasses the prior.
                        if (ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                                previousReliable, current, settings, prior, out double priorScore))
                        {
                            delta = prior;
                            score = priorScore;
                            source = "prior-overrode-outlier-direct";
                            outlierDirectRejected++;
                            priorResolved++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }

                        // Genuine variable wheel motion is possible. Outlier direct evidence remains
                        // admissible only with stronger consensus and exact raw-pair validation.
                        if (directAnchor.AgreementCount >= 3 &&
                            ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                                previousReliable, current, settings, directAnchor.ScrollDelta, out double outlierScore))
                        {
                            delta = directAnchor.ScrollDelta;
                            score = outlierScore;
                            source = "validated-direct-outlier";
                            directAccepted++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }

                        outlierDirectRejected++;
                    }
                }
            }

            if (hasPrior)
            {
                if (ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                        previousReliable, current, settings, prior, out double priorScore))
                {
                    delta = prior;
                    score = priorScore;
                    source = "validated-prior-v019";
                    priorResolved++;
                    Remember(delta);
                    Complete(delta, source, true);
                    return true;
                }

                bool hasNear = ShareXModVerticalFallbackMatcher.TryEstimateNearDelta(
                    previousReliable, current, settings, prior, out int nearDelta, out double nearScore);
                bool hasFull = TryFullRange(previousReliable, current, settings, out int fullDelta, out double fullScore);

                // A repeated page pattern can produce a threshold-passing candidate near the prior
                // even when the actual wheel movement was much shorter. If the global candidate is
                // materially better, prefer it. This is the exact class of ambiguity missing from
                // the v0.1.8 synthetic suite.
                if (hasFull && (!hasNear || fullScore + 1.0 < nearScore))
                {
                    delta = fullDelta;
                    score = fullScore;
                    source = "full-range-recovered-prior-outlier";
                    fullRangeRecovered++;
                    Remember(delta);
                    Complete(delta, source, true);
                    return true;
                }

                if (hasNear)
                {
                    delta = nearDelta;
                    score = nearScore;
                    source = "near-prior-v019";
                    priorResolved++;
                    Remember(delta);
                    Complete(delta, source, true);
                    return true;
                }

                if (hasFull)
                {
                    delta = fullDelta;
                    score = fullScore;
                    source = "full-range-recovered-prior-outlier";
                    fullRangeRecovered++;
                    Remember(delta);
                    Complete(delta, source, true);
                    return true;
                }
            }
            else if (TryFullRange(previousReliable, current, settings, out delta, out score))
            {
                source = "full-range-bootstrap-v019";
                fullRangeRecovered++;
                Remember(delta);
                Complete(delta, source, true);
                return true;
            }

            terminalUnresolved++;
            source = "unresolved-no-reliable-geometry";
            Complete(0, source, false);
            return false;
        }
    }

    public static ShareXModV019TransitionTelemetry SnapshotTelemetry()
    {
        lock (Sync)
            return new ShareXModV019TransitionTelemetry(
                directAccepted, priorResolved, outlierDirectRejected, fullRangeRecovered,
                0, 0, terminalUnresolved,
                latestDelta, latestSource, false);
    }

    public static void ResetLive()
    {
        lock (Sync)
        {
            RecentAcceptedDeltas.Clear();
            directAccepted = priorResolved = outlierDirectRejected = fullRangeRecovered = 0;
            terminalUnresolved = 0;
            latestDelta = 0;
            latestSource = "none";
        }
    }

    internal static void SeedForSelfTest(params int[] deltas)
    {
        lock (Sync)
        {
            RecentAcceptedDeltas.Clear();
            foreach (int value in deltas.Where(x => x > 0)) Remember(value);
        }
    }

    private static bool TryFullRange(
        Bitmap previous,
        Bitmap current,
        ShareXModRobustScrollingSettings settings,
        out int delta,
        out double score)
    {
        delta = 0;
        score = double.MaxValue;
        if (!ShareXModVerticalFallbackMatcher.TryEstimateScrollDeltaOnly(
                previous, current, settings, out int candidate, out double candidateScore))
            return false;

        if (candidate <= 0 || candidate >= current.Height)
            return false;

        if (!ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                previous, current, settings, candidate, out double validatedScore))
            return false;

        delta = candidate;
        score = Math.Min(candidateScore, validatedScore);
        return true;
    }

    private static void Remember(int delta)
    {
        if (delta <= 0) return;
        RecentAcceptedDeltas.Enqueue(delta);
        while (RecentAcceptedDeltas.Count > 7) RecentAcceptedDeltas.Dequeue();
    }

    private static bool TryGetStablePrior(out int prior)
    {
        prior = 0;
        if (RecentAcceptedDeltas.Count == 0) return false;
        int[] values = RecentAcceptedDeltas.OrderBy(x => x).ToArray();
        int median = values[values.Length / 2];
        int tolerance = Math.Max(40, median / 10);
        int clustered = values.Count(x => Math.Abs(x - median) <= tolerance);
        if (values.Length >= 3 && clustered < (values.Length + 1) / 2) return false;
        prior = median;
        return prior > 0;
    }

    private static void Complete(int delta, string source, bool resolved)
    {
        latestDelta = delta;
        latestSource = source;
        TryWriteEvidence(delta, source, resolved);
    }

    private static void TryWriteEvidence(int delta, string source, bool resolved)
    {
        try
        {
            string? root = ShareXModCaptureSessionContext.CurrentRootDirectory;
            if (string.IsNullOrWhiteSpace(root)) return;
            string directory = Path.Combine(root, "geometry-v019");
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("geometry-v019", directory);
            File.AppendAllText(
                Path.Combine(directory, "transitions.jsonl"),
                JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.Now,
                    resolved,
                    held = false,
                    delta,
                    source,
                    telemetry = SnapshotTelemetry()
                }) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }
}
