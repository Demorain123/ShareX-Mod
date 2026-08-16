#nullable enable

using System;
using System.Drawing;

namespace ShareX.ScreenCaptureLib;

/// <summary>
/// RC4 regressions derived from the real v0.1.9-rc3 Linux.do failure without embedding user pixels.
/// The real sequence established a stable ~752px temporal prior, then lost the direct anchor. The
/// exact prior still validated strongly, but a larger ~1068px full-range alias won a shorter-overlap
/// search and was allowed to veto the prior, causing an immediate terminal unresolved stop.
///
/// The same capture also exposed a fixed-control edge case: when the document region revealed behind
/// a repeatedly stationary bottom-right control is visually quiet/blank, the RC3 compositor's source
/// stationarity guard refuses to repair the provisional tail. That leaves one fixed Back/counter copy
/// per scroll step. RC4 permits that repair only for repeatedly confirmed bottom-right components;
/// right-edge-only surfaces retain the conservative source-stationarity guard.
/// </summary>
internal static class ShareXModV019Rc4RealEvidenceSelfTests
{
    private const int FixedWidth = 900;
    private const int FixedHeight = 720;
    private const int FixedDelta = 390;

    public static string RunOrThrow()
    {
        VerifyUncorroboratedUpwardAliasCannotVetoValidatedPrior();
        VerifyShortConflictingCandidateStillVetoesPrior();
        VerifyNearPriorCandidateDoesNotCreateFalseDisagreement();
        VerifyBlankSourceBottomFixedControlsDoNotAccumulate();
        return "v0.1.9 rc4 real-evidence passed: upward full-range alias cannot veto validated prior, short conflicting evidence still vetoes, near-prior agreement remains accepted, blank-source bottom fixed controls stay bounded, premature no-direct stop regression blocked.";
    }

    private static void VerifyUncorroboratedUpwardAliasCannotVetoValidatedPrior()
    {
        // Scaled directly from the rc3 evidence: stable prior ~= 752, spurious full-range ~= 1068.
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(1068, 752))
        {
            throw new InvalidOperationException(
                "RC4 regression: an uncorroborated larger full-range alias was allowed to veto a validated prior.");
        }
    }

    private static void VerifyShortConflictingCandidateStillVetoesPrior()
    {
        // This preserves the confidence-loop regression that forced RC3 to stop blindly trusting a
        // plausible prior: a real 120px short movement must be allowed to challenge a 260px prior.
        if (!ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(120, 260))
        {
            throw new InvalidOperationException(
                "RC4 regression: short conflicting evidence no longer vetoes a potentially aliased prior.");
        }
    }

    private static void VerifyNearPriorCandidateDoesNotCreateFalseDisagreement()
    {
        if (ShareXModTransitionResolverV019.ShouldFullRangeVetoValidatedPrior(748, 752))
        {
            throw new InvalidOperationException(
                "RC4 regression: a near-prior candidate was incorrectly treated as conflicting geometry.");
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
