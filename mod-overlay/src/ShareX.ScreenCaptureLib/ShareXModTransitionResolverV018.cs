#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal readonly record struct ShareXModV018TransitionTelemetry(
    int NormalResolved,
    int GapHeld,
    int CatchUpResolved,
    int TerminalUnresolved,
    int LatestDelta,
    string LatestSource,
    bool PendingGap);

/// <summary>
/// v0.1.8 transition policy inspired by robust scrolling stitchers that keep the last reliable
/// reference when one frame cannot be aligned. The first unresolved transition is non-destructive:
/// the mosaic and reliable reference are held. The following frame may catch up by validating a
/// two-step delta. If that also fails, the capture stops; there is no legacy mosaic fallback.
/// </summary>
internal static class ShareXModTransitionResolverV018
{
    private static readonly object Sync = new();
    private static bool pendingGap;
    private static int baseDelta;
    private static int normalResolved;
    private static int gapHeld;
    private static int catchUpResolved;
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

            if (pendingGap)
            {
                int expected = checked(baseDelta * 2);
                int tolerance = Math.Max(24, Math.Max(1, baseDelta) / 10);
                bool directCatchUp =
                    hasDirectAnchor &&
                    directAnchor.ScrollDelta > 0 &&
                    directAnchor.ScrollDelta < current.Height &&
                    Math.Abs(directAnchor.ScrollDelta - expected) <= tolerance &&
                    directAnchor.AgreementCount >= 2;

                if (directCatchUp)
                {
                    delta = directAnchor.ScrollDelta;
                    score = directAnchor.Score;
                    source = "two-step-direct-catch-up";
                    pendingGap = false;
                    catchUpResolved++;
                    Complete(delta, source, true, false);
                    return true;
                }

                ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();
                if (expected > 0 && expected < current.Height &&
                    ShareXModVerticalFallbackMatcher.TryValidateSpecificDelta(
                        previousReliable, current, settings, expected, out double catchUpScore))
                {
                    delta = expected;
                    score = catchUpScore;
                    source = "two-step-validated-catch-up";
                    pendingGap = false;
                    catchUpResolved++;
                    Complete(delta, source, true, false);
                    return true;
                }

                pendingGap = false;
                terminalUnresolved++;
                source = expected >= current.Height
                    ? "catch-up-impossible-overlap-too-small"
                    : "catch-up-validation-failed";
                Complete(0, source, false, false);
                return false;
            }

            if (ShareXModAnchorContinuityV017.TryResolve(
                    previousReliable,
                    current,
                    hasDirectAnchor,
                    directAnchor,
                    out delta,
                    out source,
                    out score))
            {
                baseDelta = delta;
                normalResolved++;
                Complete(delta, source, true, false);
                return true;
            }

            ShareXModV017AnchorTelemetry previousTelemetry = ShareXModAnchorContinuityV017.SnapshotTelemetry();
            int candidateBase = previousTelemetry.LatestDelta;
            if (candidateBase <= 0)
            {
                // LatestDelta is zero after an unresolved v0.1.7 attempt. Reuse our last accepted
                // base delta instead; it is updated only by an actually accepted transition.
                candidateBase = baseDelta;
            }

            if (candidateBase > 0 && candidateBase * 2 < current.Height)
            {
                baseDelta = candidateBase;
                pendingGap = true;
                gapHeld++;
                holdReliableReference = true;
                source = "hold-one-gap-for-catch-up";
                Complete(0, source, false, true);
                return false;
            }

            terminalUnresolved++;
            source = candidateBase > 0
                ? "unresolved-no-safe-catch-up-overlap"
                : "unresolved-no-reliable-scroll-prior";
            Complete(0, source, false, false);
            return false;
        }
    }

    public static ShareXModV018TransitionTelemetry SnapshotTelemetry()
    {
        lock (Sync)
            return new ShareXModV018TransitionTelemetry(
                normalResolved, gapHeld, catchUpResolved, terminalUnresolved,
                latestDelta, latestSource, pendingGap);
    }

    public static void ResetLive()
    {
        lock (Sync)
        {
            pendingGap = false;
            baseDelta = 0;
            normalResolved = gapHeld = catchUpResolved = terminalUnresolved = 0;
            latestDelta = 0;
            latestSource = "none";
        }
    }

    internal static void ForcePendingGapForSelfTest(int reliableBaseDelta)
    {
        lock (Sync)
        {
            baseDelta = reliableBaseDelta;
            pendingGap = reliableBaseDelta > 0;
        }
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
            string directory = Path.Combine(root, "trust-split-v018");
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("trust-split-v018", directory);
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
