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
    // Keep Required out of the positional constructor so old recorded recipes and every existing
    // constructor remain compatible. Requiredness is derived from semantic intent/failure policy.
    // Page checkpoints/ranges/navigation are safety-critical. User-demonstrated expansions can be
    // optional when their policy explicitly says to skip; horizontal content is retained unless a
    // future recipe explicitly marks it skippable.
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
    bool Trusted);
