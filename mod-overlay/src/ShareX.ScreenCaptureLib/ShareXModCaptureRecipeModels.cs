#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShareX.ScreenCaptureLib;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ShareXModCaptureRecipeStepKind
{
    PageCheckpoint,
    CaptureVerticalRange,
    HorizontalSweep,
    ExpandOrActivate,
    NextPage,
    WaitStable
}

internal sealed record ShareXModRecipeLocator(
    string Tag,
    string Id,
    string TestId,
    string Role,
    string AriaLabel,
    string Name,
    string Text,
    string Href,
    string Rel,
    string Type,
    double DocumentX,
    double DocumentY,
    double Width,
    double Height,
    string Fingerprint);

internal sealed record ShareXModRecipePageState(
    string Url,
    string Title,
    double ScrollX,
    double ScrollY,
    double DocumentWidth,
    double DocumentHeight,
    double ViewportWidth,
    double ViewportHeight,
    DateTimeOffset ObservedAt);

internal sealed record ShareXModCaptureRecipeStep(
    int Index,
    ShareXModCaptureRecipeStepKind Kind,
    string PageKey,
    double StartY,
    double EndY,
    double StartX,
    double EndX,
    ShareXModRecipeLocator? Locator,
    bool RequiresStableAfter,
    string[] Evidence,
    string FailurePolicy)
{
    [JsonIgnore]
    public bool Required => Kind switch
    {
        ShareXModCaptureRecipeStepKind.PageCheckpoint => true,
        ShareXModCaptureRecipeStepKind.CaptureVerticalRange => true,
        ShareXModCaptureRecipeStepKind.NextPage => true,
        ShareXModCaptureRecipeStepKind.HorizontalSweep =>
            !FailurePolicy.Contains("skip", StringComparison.OrdinalIgnoreCase),
        ShareXModCaptureRecipeStepKind.ExpandOrActivate =>
            !FailurePolicy.Contains("skip", StringComparison.OrdinalIgnoreCase),
        ShareXModCaptureRecipeStepKind.WaitStable => false,
        _ => false
    };
}

internal sealed record ShareXModCaptureRecipeBoundary(
    string Kind,
    double? DocumentY,
    string? AnchorFingerprint,
    int? MaxSteps,
    int? MaxDurationSeconds,
    int? MaxUnchangedPasses,
    string Description);

internal sealed record ShareXModCaptureRecipe(
    string Format,
    string Version,
    string? SessionId,
    DateTimeOffset Created,
    string Source,
    ShareXModCaptureRecipeBoundary StartBoundary,
    ShareXModCaptureRecipeBoundary StopBoundary,
    IReadOnlyList<ShareXModRecipePageState> Pages,
    IReadOnlyList<ShareXModCaptureRecipeStep> Steps,
    int RawEventCount,
    string[] Notes);

internal sealed record ShareXModRecipeRawEvent(
    long Sequence,
    string Kind,
    double Timestamp,
    ShareXModRecipePageState Page,
    ShareXModRecipeLocator? Target,
    double ScrollX,
    double ScrollY,
    double ScrollWidth,
    double ScrollHeight,
    double ClientWidth,
    double ClientHeight,
    bool IsDocumentScroller,
    bool Trusted)
{
    // These are lightweight semantic samples near the visible top/bottom edges. They are captured
    // only from a few viewport points, not by scanning the whole DOM, and let compiled vertical
    // ranges behave like document-relative placeholders when content above them later shifts.
    public ShareXModRecipeLocator? ViewportTopAnchor { get; init; }
    public ShareXModRecipeLocator? ViewportBottomAnchor { get; init; }
}
