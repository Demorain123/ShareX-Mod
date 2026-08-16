#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// v0.1.8 keeps v0.1.7's anchor-continuity resolver as a lower-level motion primitive, but retires
/// v0.1.7's destructive fixed-overlay compositor acceptance. This regression therefore validates
/// only the motion-continuity contract: direct-anchor rejection can be recovered by validated
/// fallback/prior evidence without changing the expected scroll delta.
/// </summary>
internal static class ShareXModV017AnchorContinuitySelfTests
{
    private const int Width = 900;
    private const int Height = 640;
    private const int Delta = 240;
    private const int Frames = 10;
    private static readonly int[] ForcedDirectAnchorFailures = { 2, 5, 8 };

    public static string RunOrThrow()
    {
        ShareXModAnchorContinuityV017.ResetLive();
        Bitmap? previous = null;
        int recovered = 0;
        try
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                using Bitmap current = BuildViewport(frame * Delta);
                if (previous is null)
                {
                    previous = (Bitmap)current.Clone();
                    continue;
                }

                bool forceFailure = Array.IndexOf(ForcedDirectAnchorFailures, frame) >= 0;
                ShareXModAnchorMatch direct = new(Delta, 0.2, 4);
                bool resolved = ShareXModAnchorContinuityV017.TryResolve(
                    previous,
                    current,
                    !forceFailure,
                    direct,
                    out int resolvedDelta,
                    out string source,
                    out double score);

                if (!resolved)
                    throw new InvalidOperationException($"v0.1.7 motion continuity failed at frame {frame}.");
                if (Math.Abs(resolvedDelta - Delta) > 12)
                    throw new InvalidOperationException($"v0.1.7 motion delta {resolvedDelta}, expected about {Delta}; source={source}, score={score:F2}.");
                if (forceFailure)
                {
                    if (source == "direct-anchor")
                        throw new InvalidOperationException("Forced direct-anchor rejection incorrectly reported direct-anchor source.");
                    recovered++;
                }

                previous.Dispose();
                previous = (Bitmap)current.Clone();
            }

            ShareXModV017AnchorTelemetry telemetry = ShareXModAnchorContinuityV017.SnapshotTelemetry();
            if (recovered != ForcedDirectAnchorFailures.Length)
                throw new InvalidOperationException($"v0.1.7 recovery count mismatch {recovered}/{ForcedDirectAnchorFailures.Length}.");
            if (telemetry.FallbackAnchors + telemetry.PriorValidatedAnchors < recovered)
                throw new InvalidOperationException($"v0.1.7 recovery telemetry mismatch: {telemetry}.");
            if (telemetry.UnresolvedTransitions != 0)
                throw new InvalidOperationException($"v0.1.7 motion fixture left {telemetry.UnresolvedTransitions} unresolved transitions.");

            return $"v0.1.7 motion-continuity compatibility passed: frames={Frames}, forcedDirectFailures={recovered}, unresolved=0; fixed-overlay output acceptance retired by v0.1.8 trust split.";
        }
        finally
        {
            previous?.Dispose();
            ShareXModAnchorContinuityV017.ResetLive();
        }
    }

    private static Bitmap BuildViewport(int logicalOffset)
    {
        Bitmap bitmap = new(Width, Height);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        for (int y = 0; y < Height; y += 10)
        {
            int row = (logicalOffset + y) / 10;
            using var bg = new SolidBrush(Color.FromArgb(
                232 + row * 7 % 21,
                234 + row * 11 % 19,
                236 + row * 13 % 17));
            g.FillRectangle(bg, 0, y, Width, 10);
            using var ink = new SolidBrush(Color.FromArgb(
                35 + row * 17 % 160,
                45 + row * 19 % 150,
                55 + row * 23 % 140));
            g.FillRectangle(ink, 30 + row * 29 % 650, y + 3, 80 + row * 3 % 150, 4);
        }
        return bitmap;
    }
}
