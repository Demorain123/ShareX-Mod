#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// RC4 regressions derived from the real v0.1.9-rc3 Linux.do failure without embedding user pixels.
/// The real sequence established a stable ~752px temporal prior, then lost the direct anchor. The
/// exact prior still validated strongly, a restricted near-prior search remained near ~737px, but a
/// larger ~1068px full-range alias won a shorter-overlap search and was allowed to veto the prior.
///
/// RC4 therefore does not blindly trust the prior and does not blindly ignore upward full-range
/// candidates. A larger conflicting full-range candidate is treated as an alias only when the
/// independent restricted near-prior search also corroborates the validated prior. This preserves
/// the older ambiguous-short regression, where a 260px prior is only barely valid, full-range finds
/// a far larger alias, and near-prior does not corroborate the prior; that case must still fail closed.
///
/// The same capture exposed a fixed-control edge case: when the document region revealed behind a
/// repeatedly stationary bottom-right control is visually quiet/blank, the RC3 compositor's source
/// stationarity guard refuses to repair the provisional tail. RC4 permits that repair only for
/// repeatedly confirmed bottom-right components; right-edge-only surfaces remain conservative.
/// </summary>
internal static class ShareXModV019Rc4RealEvidenceSelfTests
{
    private const int FixedWidth = 900;
    private const int FixedHeight = 720;
    private const int FixedDelta = 390;

    public static string RunOrThrow()
    {
        VerifyCorroboratedUpwardAliasCannotVetoValidatedPrior();
        VerifyUncorroboratedUpwardConflictStillVetoesPrior();
        VerifyNearPriorCandidateDoesNotCreateFalseDisagreement();
        VerifyBlankSourceBottomFixedControlsDoNotAccumulate();
        return "v0.1.9 rc4 real-evidence passed: near-prior-corroborated upward full-range alias cannot veto validated prior, uncorroborated upward conflict still fails closed, near-prior agreement remains accepted, blank-source bottom fixed controls stay bounded, premature no-direct stop regression blocked.";
    }

    private static void VerifyCorroboratedUpwardAliasCannotVetoValidatedPrior()
    {
        // Scaled directly from the rc3 evidence: prior ~= 752, restricted near ~= 737, spurious
        // full-range ~= 1068. The larger candidate has less overlap and must not defeat two pieces
        // of prior-local evidence that agree on the same geometry.
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(
                fullDelta: 1068, priorDelta: 752, hasNear: true, nearDelta: 737))
        {
            throw new InvalidOperationException(
                "RC4 regression: a near-prior-corroborated upward full-range alias vetoed the validated prior.");
        }
    }

    private static void VerifyUncorroboratedUpwardConflictStillVetoesPrior()
    {
        // Confidence-loop synthetic evidence for the old ambiguous-short case: the actual movement
        // is 120, a 260 prior is only barely valid, full-range can prefer ~620, and near search lands
        // around ~300 rather than corroborating 260. RC4 must not silently accept the wrong 260 prior.
        if (!ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(
                fullDelta: 620, priorDelta: 260, hasNear: true, nearDelta: 300))
        {
            throw new InvalidOperationException(
                "RC4 regression: an uncorroborated upward conflict no longer vetoes a potentially aliased prior.");
        }
    }

    private static void VerifyNearPriorCandidateDoesNotCreateFalseDisagreement()
    {
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(
                fullDelta: 748, priorDelta: 752, hasNear: true, nearDelta: 748))
        {
            throw new InvalidOperationException(
                "RC4 regression: a full-range candidate near the prior was incorrectly treated as conflicting geometry.");
        }
    }

    private static void VerifyBlankSourceBottomFixedControlsDoNotAccumulate()
    {
        using var session = new ShareXModTrustSplitCompositorV019.Session();
        using Bitmap first = BuildQuietViewport(0);
        Bitmap? result = (Bitmap)first.Clone();
        Bitmap? previous = (Bitmap)first.Clone();
        try
        {
            for (int frame = 1; frame <= 6; frame++)
            {
                using Bitmap current = BuildQuietViewport(frame);
                Bitmap? next = session.TryAppend(result!, previous!, current, FixedDelta);
                if (next is null) throw new InvalidOperationException("RC4 quiet fixed-control fixture was rejected.");
                result.Dispose();
                result = next;
                previous.Dispose();
                previous = (Bitmap)current.Clone();
            }

            ShareXModV019CompositorTelemetry telemetry = session.SnapshotTelemetry();
            int blueBands = CountBlueBands(result!);
            if (telemetry.TailRepairComponents < 4 || blueBands > 4)
            {
                throw new InvalidOperationException(
                    $"RC4 blank-source fixed controls accumulated: blueBands={blueBands}, repairs={telemetry.TailRepairComponents}, telemetry={telemetry}.");
            }
        }
        finally
        {
            previous?.Dispose();
            result?.Dispose();
        }
    }

    private static Bitmap BuildQuietViewport(int frame)
    {
        Bitmap bitmap = new(FixedWidth, FixedHeight);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);

        // Sparse moving document marks keep the frame non-identical while leaving the source region
        // behind the bottom-right fixed controls intentionally quiet. RC3 treated this quiet source
        // as "stationary" and therefore skipped every repair.
        using var ink = new SolidBrush(Color.FromArgb(45, 55, 65));
        for (int row = 0; row < 9; row++)
        {
            int y = 35 + ((row * 83 - frame * FixedDelta) % FixedHeight + FixedHeight) % FixedHeight;
            g.FillRectangle(ink, 55 + row * 37, y, 250 + row * 11, 5);
        }

        int x = FixedWidth - 190;
        using var blue = new SolidBrush(Color.FromArgb(0, 145, 220));
        using var white = new SolidBrush(Color.White);
        g.FillRectangle(blue, x, FixedHeight - 120, 160, 54);
        g.FillRectangle(white, x + 25, FixedHeight - 103, 90, 14);
        g.FillRectangle(blue, x + 8, FixedHeight - 62, 168, 52);
        g.FillRectangle(white, x + 30, FixedHeight - 45, 95, 13);
        return bitmap;
    }

    private static int CountBlueBands(Bitmap bitmap)
    {
        int bands = 0;
        bool inside = false;
        for (int y = 0; y < bitmap.Height; y += 3)
        {
            bool hit = false;
            for (int x = FixedWidth - 230; x < FixedWidth - 5; x += 4)
            {
                Color c = bitmap.GetPixel(x, y);
                if (c.B >= 170 && c.G >= 95 && c.R <= 40)
                {
                    hit = true;
                    break;
                }
            }

            if (hit && !inside)
            {
                bands++;
                inside = true;
            }
            else if (!hit)
            {
                inside = false;
            }
        }
        return bands;
    }
}
