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
/// from the temporal prior. Every accepted delta normally survives raw-pair validation. The one
/// exception is a deliberately narrow two-source consensus rule added after the real v0.1.9-rc2
/// evidence: when a stable temporal prior and a high-consensus, low-score direct anchor independently
/// agree on the same displacement, fixed/dynamic rows are not allowed to veto that geometry merely
/// because a whole-overlap RGB score is polluted. A stable prior is useful evidence, but repeated
/// content can make both prior and global searches produce plausible aliases. Escapes from a validated
/// prior remain asymmetric: a materially better shorter movement may override it, while a much larger
/// jump requires corroboration from a high-consensus direct anchor plus the independent full-range
/// matcher. If the remaining mechanisms disagree, the resolver fails closed rather than appending
/// geometry that can corrupt the committed long image.
/// </summary>
internal static class ShareXModTransitionResolverV019
{
    private static readonly object Sync = new();
    private static readonly Queue<int> RecentAcceptedDeltas = new();
    private const int StrongDirectAgreement = 3;
    private const double StrongDirectMaxAnchorScore = 8.5;

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

                    if (nearPrior)
                    {
                        // RC2 real evidence: prior ~= 750, direct=748, agreement=3, anchor score=4.38.
                        // The old whole-height RGB validator rejected 748 because sticky/dynamic rows
                        // polluted every horizontal tile, then a repeated-content 253px candidate won.
                        // Temporal prior + independent multi-anchor consensus is already two-source
                        // geometry evidence, so this narrowly-scoped agreement may not be displaced by
                        // a short global alias. Bootstrap still requires pixel validation.
                        if (directAnchor.AgreementCount >= StrongDirectAgreement &&
                            directAnchor.Score <= StrongDirectMaxAnchorScore)
                        {
                            delta = directAnchor.ScrollDelta;
                            score = directAnchor.Score;
                            source = "strong-direct-prior-consensus-v019";
                            directAccepted++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }

                        if (directAnchor.AgreementCount >= 2 &&
                            ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
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

                        double strongDirectScore = double.MaxValue;
                        bool strongDirectValid = false;
                        if (directAnchor.AgreementCount >= StrongDirectAgreement)
                        {
                            strongDirectValid = ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                                previousReliable, current, settings, directAnchor.ScrollDelta, out strongDirectScore);
                        }

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

                        // If the prior itself cannot be validated, an exact high-consensus direct
                        // candidate is still admissible when it is not an upward escape. A larger
                        // direct candidate has already had its chance above and therefore cannot
                        // bypass the independent-global corroboration requirement.
                        if (strongDirectValid && IsSafeUncorroboratedPriorCandidate(directAnchor.ScrollDelta, prior))
                        {
                            delta = directAnchor.ScrollDelta;
                            score = strongDirectScore;
                            source = "validated-direct-outlier-safe-direction";
                            directAccepted++;
                            Remember(delta);
                            Complete(delta, source, true);
                            return true;
                        }

                        // Likewise, one global search may recover a same-size/shorter move, but it
                        // cannot justify an upward escape from the temporal prior by itself.
                        if (globalValid && IsSafeUncorroboratedPriorCandidate(globalDelta, prior))
                        {
                            delta = globalDelta;
                            score = globalScore;
                            source = "full-range-recovered-prior-outlier-safe-direction";
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

                // A robust prior score can itself be a repeated-pattern alias. If an independent
                // full-range search points to a materially different displacement and did not earn
                // the strict short-override rule above, the evidence is ambiguous. Do not silently
                // choose the temporal prior merely because both scores happen to be below threshold.
                bool fullDisagreesWithPrior = hasFull && !AreIndependentCandidatesConsistent(fullDelta, prior);
                if (priorValid && !fullDisagreesWithPrior)
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
                bool safeFull = hasFull && IsSafeUncorroboratedPriorCandidate(fullDelta, prior);
                bool safeNear = hasNear && IsSafeUncorroboratedPriorCandidate(nearDelta, prior);
                bool nearCorroboratesFull = safeFull && safeNear &&
                    AreIndependentCandidatesConsistent(fullDelta, nearDelta);

                // If a restricted near-prior search produces a hypothesis while the independent
                // full-range search points somewhere else, that disagreement is evidence of aliasing,
                // not permission to choose whichever score happens to be lower. Fail closed instead.
                if (safeFull && (!safeNear || nearCorroboratesFull))
                {
                    if (nearCorroboratesFull && nearScore + 1.0 < fullScore)
                    {
                        delta = nearDelta;
                        score = nearScore;
                        source = "near-prior-full-range-confirmed-v019";
                        priorResolved++;
                    }
                    else
                    {
                        delta = fullDelta;
                        score = fullScore;
                        source = nearCorroboratesFull
                            ? "full-range-near-prior-confirmed-v019"
                            : "full-range-recovered-prior-outlier-safe-direction";
                        fullRangeRecovered++;
                    }
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

    private static bool IsSafeUncorroboratedPriorCandidate(int candidateDelta, int priorDelta)
    {
        if (candidateDelta <= 0 || priorDelta <= 0) return false;
        // With no direct corroboration an increase above a stable temporal prior is never accepted.
        // Real larger movements can still succeed through the high-consensus-direct + global path.
        return candidateDelta <= priorDelta;
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
