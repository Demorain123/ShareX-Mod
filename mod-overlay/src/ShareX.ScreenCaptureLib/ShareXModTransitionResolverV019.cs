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
/// v0.1.9 geometry resolver. Real v0.1.8 Linux.do evidence exposed two separate failure modes:
/// low-consensus direct anchors could jump to a visually similar short offset, while a legitimate
/// one-off short wheel movement was rejected because the temporal prior was treated as the only
/// admissible search neighbourhood. v0.1.9 therefore treats every geometry source as evidence:
/// a stable prior can override a conflicting direct anchor, an outlier direct anchor must validate
/// against the raw pair, and a prior miss may fall back to a full-range raw-pixel search. If all of
/// those fail, validate-before-scroll lets one same-position retry occur without advancing the page.
/// There is still no legacy mosaic matcher fallback.
/// </summary>
internal static class ShareXModTransitionResolverV019
{
    private static readonly object Sync = new();
    private static readonly Queue<int> RecentAcceptedDeltas = new();

    private static bool pendingRetry;
    private static int retryExpectedDelta;
    private static int directAccepted;
    private static int priorResolved;
    private static int outlierDirectRejected;
    private static int fullRangeRecovered;
    private static int retryHeld;
    private static int retryResolved;
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

            // The manager does not issue another scroll while this state is pending, so this is a
            // second look at the same page position after extra settle time, not a two-step catch-up.
            if (pendingRetry)
            {
                int expected = retryExpectedDelta;
                pendingRetry = false;

                if (TryResolveExpected(previousReliable, current, hasDirectAnchor, directAnchor,
                        settings, expected, out delta, out source, out score))
                {
                    source = source.StartsWith("direct", StringComparison.Ordinal)
                        ? "settle-retry-direct"
                        : "settle-retry-validated-prior";
                    retryResolved++;
                    Remember(delta);
                    Complete(delta, source, true, false);
                    return true;
                }

                // A second full-range pass is useful when dynamic content settled into a different
                // but now unambiguous geometry. It must still pass the raw-pixel validator.
                if (TryFullRange(previousReliable, current, settings, out delta, out score))
                {
                    source = "settle-retry-full-range";
                    retryResolved++;
                    fullRangeRecovered++;
                    Remember(delta);
                    Complete(delta, source, true, false);
                    return true;
                }

                terminalUnresolved++;
                source = "settle-retry-unresolved";
                Complete(0, source, false, false);
                return false;
            }

            bool hasPrior = TryGetStablePrior(out int prior);

            if (hasDirectAnchor && directAnchor.ScrollDelta > 0 && directAnchor.ScrollDelta < current.Height)
            {
                if (!hasPrior)
                {
                    // Bootstrap direct evidence still needs at least two agreeing bands.
                    if (directAnchor.AgreementCount >= 2 &&
                        ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                            previousReliable, current, settings, directAnchor.ScrollDelta, out double directScore))
                    {
                        delta = directAnchor.ScrollDelta;
                        score = directScore;
                        source = "validated-direct-bootstrap";
                        directAccepted++;
                        Remember(delta);
                        Complete(delta, source, true, false);
                        return true;
                    }
                }
                else
                {
                    int tolerance = Math.Max(48, prior / 8);
                    bool nearPrior = Math.Abs(directAnchor.ScrollDelta - prior) <= tolerance;

                    if (nearPrior && directAnchor.AgreementCount >= 2)
                    {
                        // A near-prior anchor is still validated against the complete raw-frame pair;
                        // this rejects accidental repeated-pattern aliases before they enter geometry.
                        if (ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                                previousReliable, current, settings, directAnchor.ScrollDelta, out double directScore))
                        {
                            delta = directAnchor.ScrollDelta;
                            score = directScore;
                            source = "validated-direct-near-prior";
                            directAccepted++;
                            Remember(delta);
                            Complete(delta, source, true, false);
                            return true;
                        }
                    }
                    else
                    {
                        // v0.1.8 accepted 200/404px low-consensus anchors in a run whose real motion
                        // was ~750px. Prefer a stable prior when the raw pair validates it.
                        if (ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                                previousReliable, current, settings, prior, out double priorScore))
                        {
                            delta = prior;
                            score = priorScore;
                            source = "prior-overrode-outlier-direct";
                            outlierDirectRejected++;
                            priorResolved++;
                            Remember(delta);
                            Complete(delta, source, true, false);
                            return true;
                        }

                        // A real variable scroll amount is allowed, but only with stronger direct
                        // consensus plus exact raw-pair validation.
                        if (directAnchor.AgreementCount >= 3 &&
                            ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                                previousReliable, current, settings, directAnchor.ScrollDelta, out double outlierScore))
                        {
                            delta = directAnchor.ScrollDelta;
                            score = outlierScore;
                            source = "validated-direct-outlier";
                            directAccepted++;
                            Remember(delta);
                            Complete(delta, source, true, false);
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
                    Complete(delta, source, true, false);
                    return true;
                }

                if (ShareXModVerticalFallbackMatcher.TryEstimateNearDelta(
                        previousReliable, current, settings, prior, out int nearDelta, out double nearScore))
                {
                    delta = nearDelta;
                    score = nearScore;
                    source = "near-prior-v019";
                    priorResolved++;
                    Remember(delta);
                    Complete(delta, source, true, false);
                    return true;
                }
            }

            // Crucial v0.1.9 difference: a stable prior is not a prison. Real wheel scrolling can
            // occasionally move a much shorter distance. A full-range candidate is accepted only
            // after the same raw-pixel validator confirms the exact candidate delta.
            if (TryFullRange(previousReliable, current, settings, out delta, out score))
            {
                source = hasPrior ? "full-range-recovered-prior-outlier" : "full-range-bootstrap-v019";
                fullRangeRecovered++;
                Remember(delta);
                Complete(delta, source, true, false);
                return true;
            }

            int retryDelta = hasPrior ? prior : GetLastAcceptedDelta();
            if (retryDelta > 0 && retryDelta < current.Height)
            {
                retryExpectedDelta = retryDelta;
                pendingRetry = true;
                retryHeld++;
                holdReliableReference = true;
                source = "hold-current-position-for-settle-retry";
                Complete(0, source, false, true);
                return false;
            }

            terminalUnresolved++;
            source = "unresolved-no-reliable-geometry";
            Complete(0, source, false, false);
            return false;
        }
    }

    public static ShareXModV019TransitionTelemetry SnapshotTelemetry()
    {
        lock (Sync)
            return new ShareXModV019TransitionTelemetry(
                directAccepted, priorResolved, outlierDirectRejected, fullRangeRecovered,
                retryHeld, retryResolved, terminalUnresolved,
                latestDelta, latestSource, pendingRetry);
    }

    public static void ResetLive()
    {
        lock (Sync)
        {
            RecentAcceptedDeltas.Clear();
            pendingRetry = false;
            retryExpectedDelta = 0;
            directAccepted = priorResolved = outlierDirectRejected = fullRangeRecovered = 0;
            retryHeld = retryResolved = terminalUnresolved = 0;
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

    internal static void ForcePendingRetryForSelfTest(int expectedDelta)
    {
        lock (Sync)
        {
            retryExpectedDelta = expectedDelta;
            pendingRetry = expectedDelta > 0;
        }
    }

    private static bool TryResolveExpected(
        Bitmap previous,
        Bitmap current,
        bool hasDirectAnchor,
        ShareXModAnchorMatch directAnchor,
        ShareXModRobustScrollingSettings settings,
        int expected,
        out int delta,
        out string source,
        out double score)
    {
        delta = 0;
        source = "retry-unresolved";
        score = double.MaxValue;
        int tolerance = Math.Max(40, Math.Max(1, expected) / 10);

        if (hasDirectAnchor && directAnchor.ScrollDelta > 0 &&
            Math.Abs(directAnchor.ScrollDelta - expected) <= tolerance &&
            directAnchor.AgreementCount >= 2 &&
            ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                previous, current, settings, directAnchor.ScrollDelta, out double directScore))
        {
            delta = directAnchor.ScrollDelta;
            score = directScore;
            source = "direct-retry";
            return true;
        }

        if (expected > 0 && expected < current.Height &&
            ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                previous, current, settings, expected, out double expectedScore))
        {
            delta = expected;
            score = expectedScore;
            source = "validated-retry-prior";
            return true;
        }

        return false;
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

    private static int GetLastAcceptedDelta() => RecentAcceptedDeltas.Count == 0 ? 0 : RecentAcceptedDeltas.Last();

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

    private static void Complete(int delta, string source, bool resolved, bool held)
    {
        latestDelta = delta;
        latestSource = source;
        TryWriteEvidence(delta, source, resolved, held);
    }

    private static void TryWriteEvidence(int delta, string source, bool resolved, bool held)
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
                    held,
                    delta,
                    source,
                    telemetry = SnapshotTelemetry()
                }) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }
}
