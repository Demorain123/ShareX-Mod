#nullable enable

using System;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRecipeAnchorMath
{
    public static double ResolveBoundary(
        ShareXModCaptureRecipeStep anchorStep,
        double currentAnchorDocumentY)
    {
        // Anchor step StartY stores the anchor's document Y during demonstration.
        // EndY stores the demonstrated capture boundary Y. Their delta survives
        // normal layout movement and is applied to the re-resolved anchor.
        double recordedOffset = anchorStep.EndY - anchorStep.StartY;
        return Math.Max(0, currentAnchorDocumentY + recordedOffset);
    }
}
