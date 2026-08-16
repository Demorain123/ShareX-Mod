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
/// v0.1.9 geometry resolver derived from the real v0.1.8 Linux.do evidence. Low-consensus direct
/// anchors can jump to visually similar offsets, while legitimate wheel movements can vary sharply
/// from the temporal prior. Every accepted delta must survive raw-pair validation. A stable prior is
/// useful evidence, but repeated content can make both prior and global searches produce plausible
/// aliases. The resolver therefore treats downward escapes from a validated prior asymmetrically:
/// a materially better shorter movement may override it (important near document ends), while a
/// much larger jump requires corroboration from a high-consensus direct anchor plus the independent
/// full-range matcher. This keeps the live path fail-closed rather than following a single attractive
/// repeated-pattern score.
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
                        bool priorValid = ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                            previousReliable, current, settings, prior, out double priorScore);
                        bool globalValid = TryFullRange(
                            previousReliable, current, settings, out int globalDelta, out double globalScore);
                        bool strongDirectValid = directAnchor.AgreementCount >= 3 &&
                            ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                                previousReliable, current, settings, directAnchor.ScrollDelta, out double strongDirectScore);

                        // Larger-than-prior jumps are the dangerous direction on repeated content:
                        // a long-offset alias has less overlap and can look deceptively clean. Permit
                        // such a jump only when two independent mechanisms agree on the same region.
                        if (strongDirectValid && globalValid &&
                            AreIndependentCandidatesConsistent(directAnchor.ScrollDelta, globalDelta))
                        {
                            delta = directAnchor.ScrollDelta;
                            score = Math.Min(strongDirectScore, globalScore);
                            source = "validated-direct-outlier-global-confirmed";
                            directAccepted++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }

                        // A shorter movement can legitimately occur at the bottom of a page or when
                        // the wheel produces less motion than previous steps. It may override a prior
                        // only when the global raw-pixel evidence is materially better.
                        if (globalValid && ShouldShortGlobalOverrideValidatedPrior(
                                globalDelta, globalScore, prior, priorValid ? priorScore : double.MaxValue))
                        {
                            delta = globalDelta;
                            score = globalScore;
                            source = "short-full-range-overrode-validated-prior";
                            outlierDirectRejected++;
                            fullRangeRecovered++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }

                        // The real v0.1.8 evidence contained 200/404px false direct anchors among a
                        // strong ~750px history. A weak conflicting anchor cannot bypass a validated prior.
                        if (priorValid)
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

                        // If the prior itself cannot be validated, strong exact direct evidence is
                        // still useful; otherwise use a validated global candidate as the last
                        // fail-closed recovery option.
                        if (strongDirectValid)
                        {
                            delta = directAnchor.ScrollDelta;
                            score = strongDirectScore;
                            source = "validated-direct-outlier";
                            directAccepted++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }

                        if (globalValid)
                        {
                            delta = globalDelta;
                            score = globalScore;
                            source = "full-range-recovered-prior-outlier";
                            outlierDirectRejected++;
                            fullRangeRecovered++;
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
                bool priorValid = ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                    previousReliable, current, settings, prior, out double priorScore);
                bool hasFull = TryFullRange(previousReliable, current, settings, out int fullDelta, out double fullScore);

                // With no corroborating direct anchor, only a materially better shorter global
                // movement can displace an already validated prior. This directly addresses short
                // terminal wheel moves without opening the door to far-away repeated-pattern aliases.
                if (hasFull && ShouldShortGlobalOverrideValidatedPrior(
                        fullDelta, fullScore, prior, priorValid ? priorScore : double.MaxValue))
                {
                    delta = fullDelta;
                    score = fullScore;
                    source = "short-full-range-overrode-validated-prior";
                    fullRangeRecovered++;
                    Remember(delta);
                    Complete(delta, source, true);
                    return true;
                }

                if (priorValid)
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

    private static bool ShouldShortGlobalOverrideValidatedPrior(
        int globalDelta, double globalScore, int priorDelta, double priorScore)
    {
        if (globalDelta <= 0 || double.IsInfinity(globalScore) || double.IsNaN(globalScore)) return false;
        if (priorDelta <= 0) return true;
        int tolerance = Math.Max(48, priorDelta / 8);
        if (globalDelta >= priorDelta - tolerance) return false;
        if (double.IsInfinity(priorScore) || double.IsNaN(priorScore)) return true;

        // Require both absolute and relative improvement. This keeps a stable prior when scores are
        // merely close, while allowing a genuinely better short terminal movement to escape it.
        return globalScore + 1.0 < priorScore && globalScore <= priorScore * 0.82;
    }

    private static bool AreIndependentCandidatesConsistent(int directDelta, int globalDelta)
    {
        if (directDelta <= 0 || globalDelta <= 0) return false;
        int tolerance = Math.Clamp(Math.Max(12, directDelta / 25), 12, 40);
        return Math.Abs(directDelta - globalDelta) <= tolerance;
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
