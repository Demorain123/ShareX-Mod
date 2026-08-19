namespace LongCapture.Standalone;

internal enum BrowserAgentSpeedStrategy
{
    FixedVeryLow,
    FixedLow,
    FixedMedium,
    FixedHigh,
    FixedVeryHigh,
    AdaptiveRobust,
    AdaptiveBalanced,
    AdaptiveHighSpeed
}

internal enum BrowserAgentRepairPrecision
{
    Low,
    Medium,
    High
}

internal sealed class BrowserAgentFrameTuning
{
    public int Gear { get; init; }
    public int StartDelayMs { get; init; }
    public int StableWindowMs { get; init; }
    public int MaxWaitMs { get; init; }
    public double OverlapRatio { get; init; }
    public string Name { get; init; } = string.Empty;
}

internal sealed class BrowserAgentAdaptiveDecision
{
    public int RiskScore { get; init; }
    public string Reasons { get; init; } = string.Empty;
    public string BeforeSpeed { get; init; } = string.Empty;
    public string AfterSpeed { get; init; } = string.Empty;
    public bool RepairCandidate { get; init; }
    public bool RequestImmediateRecovery { get; init; }
}

internal static class BrowserAgentAdaptiveProfiles
{
    public static readonly string[] StrategyDisplayNames =
    [
        "Fixed · Very Low",
        "Fixed · Low",
        "Fixed · Medium",
        "Fixed · High",
        "Fixed · Very High",
        "Adaptive · Robust",
        "Adaptive · Balanced",
        "Adaptive · High speed"
    ];

    public static readonly string[] PrecisionDisplayNames = ["Low", "Medium", "High"];

    public static bool IsAdaptive(BrowserAgentSpeedStrategy strategy) => strategy >= BrowserAgentSpeedStrategy.AdaptiveRobust;

    public static BrowserAgentFrameTuning ForGear(int gear)
    {
        gear = Math.Clamp(gear, 0, 4);
        return gear switch
        {
            0 => new BrowserAgentFrameTuning { Gear = 0, Name = "Very Low", StartDelayMs = 500, StableWindowMs = 1800, MaxWaitMs = 12000, OverlapRatio = 0.45 },
            1 => new BrowserAgentFrameTuning { Gear = 1, Name = "Low", StartDelayMs = 350, StableWindowMs = 1350, MaxWaitMs = 10000, OverlapRatio = 0.40 },
            2 => new BrowserAgentFrameTuning { Gear = 2, Name = "Medium", StartDelayMs = 200, StableWindowMs = 950, MaxWaitMs = 8500, OverlapRatio = 0.34 },
            3 => new BrowserAgentFrameTuning { Gear = 3, Name = "High", StartDelayMs = 100, StableWindowMs = 700, MaxWaitMs = 7500, OverlapRatio = 0.27 },
            _ => new BrowserAgentFrameTuning { Gear = 4, Name = "Very High", StartDelayMs = 0, StableWindowMs = 520, MaxWaitMs = 6500, OverlapRatio = 0.20 }
        };
    }

    public static int TargetGear(BrowserAgentSpeedStrategy strategy) => strategy switch
    {
        BrowserAgentSpeedStrategy.FixedVeryLow => 0,
        BrowserAgentSpeedStrategy.FixedLow => 1,
        BrowserAgentSpeedStrategy.FixedMedium => 2,
        BrowserAgentSpeedStrategy.FixedHigh => 3,
        BrowserAgentSpeedStrategy.FixedVeryHigh => 4,
        BrowserAgentSpeedStrategy.AdaptiveRobust => 2,
        BrowserAgentSpeedStrategy.AdaptiveBalanced => 3,
        BrowserAgentSpeedStrategy.AdaptiveHighSpeed => 4,
        _ => 2
    };

    public static BrowserAgentFrameTuning BaseTuning(BrowserAgentSpeedStrategy strategy) => ForGear(TargetGear(strategy));

    public static int RepairRiskThreshold(BrowserAgentRepairPrecision precision) => precision switch
    {
        BrowserAgentRepairPrecision.Low => 6,
        BrowserAgentRepairPrecision.Medium => 4,
        BrowserAgentRepairPrecision.High => 3,
        _ => 4
    };

    public static int ImmediateRepairRiskThreshold(BrowserAgentRepairPrecision precision) => precision switch
    {
        BrowserAgentRepairPrecision.Low => 99,
        BrowserAgentRepairPrecision.Medium => 7,
        BrowserAgentRepairPrecision.High => 5,
        _ => 7
    };

    public static int PostReviewBudget(BrowserAgentRepairPrecision precision) => precision switch
    {
        BrowserAgentRepairPrecision.Low => 3,
        BrowserAgentRepairPrecision.Medium => 8,
        BrowserAgentRepairPrecision.High => 16,
        _ => 8
    };

    public static int PostReviewAttempts(BrowserAgentRepairPrecision precision) => precision switch
    {
        BrowserAgentRepairPrecision.Low => 1,
        BrowserAgentRepairPrecision.Medium => 2,
        BrowserAgentRepairPrecision.High => 3,
        _ => 2
    };

    public static bool SelfTest()
    {
        if (ForGear(0).StableWindowMs <= ForGear(4).StableWindowMs) return false;
        if (ForGear(0).OverlapRatio <= ForGear(4).OverlapRatio) return false;

        var controller = new BrowserAgentAdaptiveController(
            BrowserAgentSpeedStrategy.AdaptiveHighSpeed,
            BrowserAgentRepairPrecision.High);
        if (controller.Current.Gear != 4) return false;

        var risky = new BrowserAgentFrameRecord
        {
            Sequence = 3,
            StabilityWaitMs = 2400,
            StabilityPendingImages = 2,
            StabilityHeightChangeCount = 2,
            StabilityLayoutShiftScore = 0.06,
            CaptureStateChanged = true,
            OverlapVerified = true,
            OverlapMeanAbsoluteError = 2.5,
            OverlapStrongDiffRatio = 0.04,
            VisualDeltaOffsetPixels = 20
        };
        BrowserAgentAdaptiveDecision down = controller.Observe(risky);
        if (down.RiskScore < 5 || controller.Current.Gear >= 4 || !down.RepairCandidate) return false;

        int slowedGear = controller.Current.Gear;
        for (int i = 0; i < 8; i++)
        {
            controller.Observe(new BrowserAgentFrameRecord
            {
                Sequence = 10 + i,
                StabilityWaitMs = controller.Current.StableWindowMs,
                OverlapVerified = true,
                OverlapMeanAbsoluteError = 0.1,
                OverlapStrongDiffRatio = 0.001,
                VisualDeltaOffsetPixels = 0
            });
        }
        return controller.Current.Gear > slowedGear && controller.Current.Gear <= 4;
    }
}

internal sealed class BrowserAgentAdaptiveController
{
    private readonly BrowserAgentSpeedStrategy strategy;
    private readonly BrowserAgentRepairPrecision precision;
    private readonly int targetGear;
    private int currentGear;
    private int cleanStreak;

    public BrowserAgentAdaptiveController(BrowserAgentSpeedStrategy strategy, BrowserAgentRepairPrecision precision)
    {
        this.strategy = strategy;
        this.precision = precision;
        targetGear = BrowserAgentAdaptiveProfiles.TargetGear(strategy);
        currentGear = targetGear;
    }

    public bool IsAdaptive => BrowserAgentAdaptiveProfiles.IsAdaptive(strategy);
    public BrowserAgentFrameTuning Current => BrowserAgentAdaptiveProfiles.ForGear(currentGear);

    public BrowserAgentAdaptiveDecision Observe(BrowserAgentFrameRecord frame)
    {
        BrowserAgentFrameTuning before = Current;
        int risk = 0;
        var reasons = new List<string>();

        void Add(int score, string reason)
        {
            risk += score;
            reasons.Add(reason);
        }

        if (frame.StabilityTimedOut) Add(8, "stability-timeout");
        if (frame.CaptureStateChanged) Add(5, "capture-state-changed");
        if (frame.StabilityPendingImages > 0) Add(3, $"pending-images:{frame.StabilityPendingImages}");
        if (frame.StabilityHeightChangeCount > 0) Add(Math.Min(3, frame.StabilityHeightChangeCount + 1), $"height-change:{frame.StabilityHeightChangeCount}");
        if (frame.StabilityLayoutShiftScore >= 0.05) Add(4, $"layout-shift:{frame.StabilityLayoutShiftScore:F3}");
        else if (frame.StabilityLayoutShiftScore >= 0.015) Add(2, $"layout-shift:{frame.StabilityLayoutShiftScore:F3}");
        if (frame.StabilityLayoutShiftCount >= 3) Add(1, $"layout-shift-count:{frame.StabilityLayoutShiftCount}");
        if (frame.LazyWarmupGrowthCss > 2 || frame.EndConfirmationGrowthCss > 2) Add(4, "lazy-growth");
        if (frame.StabilityMutationCount >= 30) Add(2, $"mutations:{frame.StabilityMutationCount}");
        else if (frame.StabilityMutationCount >= 8) Add(1, $"mutations:{frame.StabilityMutationCount}");
        if (frame.StabilityResizeCount >= 5) Add(1, $"resizes:{frame.StabilityResizeCount}");
        if (frame.StabilityWaitMs > Math.Max(1200, before.StableWindowMs * 2)) Add(2, $"slow-settle:{frame.StabilityWaitMs}ms");
        if (frame.OverlapVerified && frame.Sequence > 2)
        {
            if (frame.OverlapMeanAbsoluteError >= 2.0) Add(4, $"overlap-mae:{frame.OverlapMeanAbsoluteError:F2}");
            else if (frame.OverlapMeanAbsoluteError >= 1.0) Add(2, $"overlap-mae:{frame.OverlapMeanAbsoluteError:F2}");
            if (frame.OverlapStrongDiffRatio >= 0.03) Add(4, $"strong-diff:{frame.OverlapStrongDiffRatio:P1}");
            else if (frame.OverlapStrongDiffRatio >= 0.012) Add(2, $"strong-diff:{frame.OverlapStrongDiffRatio:P1}");
        }
        if (Math.Abs(frame.VisualDeltaOffsetPixels) >= 18) Add(3, $"visual-correction:{frame.VisualDeltaOffsetPixels}px");
        else if (Math.Abs(frame.VisualDeltaOffsetPixels) >= 8) Add(1, $"visual-correction:{frame.VisualDeltaOffsetPixels}px");

        if (IsAdaptive)
        {
            if (risk >= 6)
            {
                currentGear = Math.Max(0, currentGear - 2);
                cleanStreak = 0;
            }
            else if (risk >= 3)
            {
                currentGear = Math.Max(0, currentGear - 1);
                cleanStreak = 0;
            }
            else if (risk == 0)
            {
                cleanStreak++;
                if (cleanStreak >= 3 && currentGear < targetGear)
                {
                    currentGear++;
                    cleanStreak = 0;
                }
            }
            else
            {
                cleanStreak = Math.Max(0, cleanStreak - 1);
            }
        }

        BrowserAgentFrameTuning after = Current;
        bool candidate = risk >= BrowserAgentAdaptiveProfiles.RepairRiskThreshold(precision);
        bool immediate = IsAdaptive && risk >= BrowserAgentAdaptiveProfiles.ImmediateRepairRiskThreshold(precision);
        return new BrowserAgentAdaptiveDecision
        {
            RiskScore = risk,
            Reasons = reasons.Count == 0 ? "clean" : string.Join(",", reasons),
            BeforeSpeed = before.Name,
            AfterSpeed = after.Name,
            RepairCandidate = candidate,
            RequestImmediateRecovery = immediate
        };
    }
}
