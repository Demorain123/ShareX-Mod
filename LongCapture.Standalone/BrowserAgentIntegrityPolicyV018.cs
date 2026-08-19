namespace LongCapture.Standalone;

internal sealed class BrowserAgentIntegrityAssessmentV018
{
    public int RiskScore { get; init; }
    public string Reasons { get; init; } = "clean";
    public bool Suspect { get; init; }
    public double ActualDeltaCss { get; init; }
    public double ExpectedDeltaCss { get; init; }
    public double DeltaErrorCss { get; init; }
    public double DeltaRobustZ { get; init; }
    public int SharedAnchors { get; init; }
    public double MaxAnchorDriftCss { get; init; }
    public string AnchorStatus { get; init; } = "not-compared";
}

internal static class BrowserAgentIntegrityPolicyV018
{
    private const double MinimumDeltaToleranceCss = 3.0;
    private const double RelativeDeltaTolerance = 0.055;
    private const double RobustZSuspect = 4.5;
    private const double AnchorDriftWarnCss = 6.0;
    private const double AnchorDriftFailCss = 18.0;

    public static BrowserAgentIntegrityAssessmentV018 Evaluate(
        IReadOnlyList<BrowserAgentFrameRecord> acceptedFrames,
        BrowserAgentFrameRecord current)
    {
        if (acceptedFrames.Count == 0)
        {
            return new BrowserAgentIntegrityAssessmentV018();
        }

        BrowserAgentFrameRecord previous = acceptedFrames[^1];
        double actualDelta = current.ScrollYCss - previous.ScrollYCss;
        double expectedDelta = previous.ScrollYAfterCss > previous.ScrollYCss + 0.5
            ? previous.ScrollYAfterCss - previous.ScrollYCss
            : Math.Max(1, previous.ViewportHeightCss * (1.0 - Math.Clamp(previous.EffectiveOverlapRatio, 0.10, 0.60)));
        double deltaError = Math.Abs(actualDelta - expectedDelta);

        List<double> historicalDeltas = acceptedFrames
            .Skip(1)
            .Select((frame, index) => frame.ScrollYCss - acceptedFrames[index].ScrollYCss)
            .Where(value => value > 0.5 && double.IsFinite(value))
            .TakeLast(16)
            .ToList();
        double robustZ = RobustZ(actualDelta, historicalDeltas);

        (int sharedAnchors, double maxAnchorDrift, string anchorStatus) = CompareAnchors(previous, current);

        int risk = 0;
        var reasons = new List<string>();
        void Add(int score, string reason)
        {
            risk += score;
            reasons.Add(reason);
        }

        double deltaTolerance = Math.Max(MinimumDeltaToleranceCss, Math.Abs(expectedDelta) * RelativeDeltaTolerance);
        if (deltaError > Math.Max(16.0, deltaTolerance * 2.5)) Add(5, $"scroll-delta-error:{deltaError:F1}px");
        else if (deltaError > deltaTolerance) Add(2, $"scroll-delta-error:{deltaError:F1}px");

        if (robustZ >= 8.0) Add(5, $"scroll-delta-outlier:z{robustZ:F1}");
        else if (robustZ >= RobustZSuspect) Add(3, $"scroll-delta-outlier:z{robustZ:F1}");

        if (current.Sequence > 2 && !current.OverlapVerified) Add(7, "overlap-unverified");
        if (current.OverlapLeftEdgeContamination || current.OverlapRightEdgeContamination) Add(5, "edge-contamination");

        if (sharedAnchors >= 1)
        {
            if (maxAnchorDrift >= AnchorDriftFailCss) Add(6, $"dom-anchor-drift:{maxAnchorDrift:F1}px");
            else if (maxAnchorDrift >= AnchorDriftWarnCss) Add(3, $"dom-anchor-drift:{maxAnchorDrift:F1}px");
        }
        else if (previous.SemanticAnchors.Count > 0 && current.SemanticAnchors.Count > 0 && current.Sequence > 2)
        {
            Add(1, "dom-anchor-no-shared-sample");
        }

        if (current.CaptureStateChanged) Add(4, "capture-state-changed");
        if (current.StabilityTimedOut) Add(6, "stability-timeout");
        if (current.StabilityPendingImages > 0) Add(2, $"pending-images:{current.StabilityPendingImages}");
        if (current.StabilityLayoutShiftScore >= 0.05) Add(4, $"layout-shift:{current.StabilityLayoutShiftScore:F3}");
        else if (current.StabilityLayoutShiftScore >= 0.015) Add(2, $"layout-shift:{current.StabilityLayoutShiftScore:F3}");
        if (current.LazyWarmupGrowthCss > 2 || current.EndConfirmationGrowthCss > 2) Add(3, "lazy-growth");

        return new BrowserAgentIntegrityAssessmentV018
        {
            RiskScore = risk,
            Reasons = reasons.Count == 0 ? "clean" : string.Join(",", reasons),
            Suspect = risk >= 3,
            ActualDeltaCss = actualDelta,
            ExpectedDeltaCss = expectedDelta,
            DeltaErrorCss = deltaError,
            DeltaRobustZ = robustZ,
            SharedAnchors = sharedAnchors,
            MaxAnchorDriftCss = maxAnchorDrift,
            AnchorStatus = anchorStatus
        };
    }

    public static List<BrowserAgentDomAnchorRecord> ReadAnchors(System.Text.Json.JsonElement state)
    {
        var anchors = new List<BrowserAgentDomAnchorRecord>();
        if (!state.TryGetProperty("semanticAnchors", out System.Text.Json.JsonElement array) ||
            array.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return anchors;
        }

        foreach (System.Text.Json.JsonElement item in array.EnumerateArray())
        {
            if (anchors.Count >= 16) break;
            string key = item.TryGetProperty("key", out System.Text.Json.JsonElement keyElement)
                ? keyElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;
            anchors.Add(new BrowserAgentDomAnchorRecord
            {
                Key = key,
                DocumentYCss = ReadNumber(item, "documentY"),
                ViewportYCss = ReadNumber(item, "viewportY"),
                HeightCss = ReadNumber(item, "height")
            });
        }
        return anchors;
    }

    public static bool SelfTest()
    {
        var first = new BrowserAgentFrameRecord
        {
            Sequence = 1,
            ScrollYCss = 0,
            ScrollYAfterCss = 320,
            ViewportHeightCss = 440,
            EffectiveOverlapRatio = 0.27,
            OverlapVerified = true,
            SemanticAnchors = new List<BrowserAgentDomAnchorRecord>
            {
                new() { Key = "post-a", DocumentYCss = 300, ViewportYCss = 300, HeightCss = 100 }
            }
        };
        var clean = new BrowserAgentFrameRecord
        {
            Sequence = 2,
            ScrollYCss = 320,
            ScrollYAfterCss = 640,
            ViewportHeightCss = 440,
            EffectiveOverlapRatio = 0.27,
            OverlapVerified = true,
            SemanticAnchors = new List<BrowserAgentDomAnchorRecord>
            {
                new() { Key = "post-a", DocumentYCss = 300.5, ViewportYCss = -19.5, HeightCss = 100 }
            }
        };
        BrowserAgentIntegrityAssessmentV018 ok = Evaluate(new List<BrowserAgentFrameRecord> { first }, clean);
        if (ok.Suspect || ok.SharedAnchors != 1 || ok.MaxAnchorDriftCss > 1.0) return false;

        var bad = new BrowserAgentFrameRecord
        {
            Sequence = 3,
            ScrollYCss = 720,
            ScrollYAfterCss = 1040,
            ViewportHeightCss = 440,
            EffectiveOverlapRatio = 0.27,
            OverlapVerified = false,
            OverlapLeftEdgeContamination = true,
            SemanticAnchors = new List<BrowserAgentDomAnchorRecord>
            {
                new() { Key = "post-a", DocumentYCss = 336, ViewportYCss = -384, HeightCss = 100 }
            }
        };
        BrowserAgentIntegrityAssessmentV018 fail = Evaluate(new List<BrowserAgentFrameRecord> { first, clean }, bad);
        return fail.Suspect && fail.RiskScore >= 10 && fail.MaxAnchorDriftCss >= 30;
    }

    private static (int Shared, double MaxDrift, string Status) CompareAnchors(
        BrowserAgentFrameRecord previous,
        BrowserAgentFrameRecord current)
    {
        if (previous.SemanticAnchors.Count == 0 || current.SemanticAnchors.Count == 0)
            return (0, 0, "no-anchor-evidence");

        Dictionary<string, BrowserAgentDomAnchorRecord> previousByKey = previous.SemanticAnchors
            .GroupBy(anchor => anchor.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        int shared = 0;
        double maxDrift = 0;
        foreach (BrowserAgentDomAnchorRecord anchor in current.SemanticAnchors)
        {
            if (!previousByKey.TryGetValue(anchor.Key, out BrowserAgentDomAnchorRecord? old)) continue;
            shared++;
            maxDrift = Math.Max(maxDrift, Math.Abs(anchor.DocumentYCss - old.DocumentYCss));
        }

        return shared == 0
            ? (0, 0, "anchors-present-no-common-key")
            : (shared, maxDrift, maxDrift >= AnchorDriftFailCss ? "anchor-drift-high" : maxDrift >= AnchorDriftWarnCss ? "anchor-drift" : "anchor-consistent");
    }

    private static double RobustZ(double value, IReadOnlyList<double> history)
    {
        if (history.Count < 5 || !double.IsFinite(value)) return 0;
        double median = Median(history);
        var deviations = history.Select(item => Math.Abs(item - median)).ToList();
        double mad = Median(deviations);
        double scale = Math.Max(1.0, mad * 1.4826);
        return Math.Abs(value - median) / scale;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        double[] ordered = values.OrderBy(value => value).ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2.0
            : ordered[middle];
    }

    private static double ReadNumber(System.Text.Json.JsonElement element, string name)
    {
        return element.TryGetProperty(name, out System.Text.Json.JsonElement value) && value.TryGetDouble(out double number)
            ? number
            : 0;
    }
}
