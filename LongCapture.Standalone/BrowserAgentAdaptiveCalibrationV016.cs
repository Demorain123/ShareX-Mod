using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentCalibrationProfile
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public int BenchmarkSamples { get; set; }
    public int FrameSamples { get; set; }
    public int CleanFrames { get; set; }
    public int RiskyFrames { get; set; }

    public double BridgeRttMs { get; set; }
    public double BridgeRttVariationMs { get; set; }
    public double CaptureMs { get; set; }
    public double CaptureVariationMs { get; set; }
    public double ActivityMs { get; set; }
    public double ActivityVariationMs { get; set; }
    public double FalseQuietMs { get; set; }
    public double FalseQuietVariationMs { get; set; }
    public double RiskScore { get; set; }
    public double OverlapMae { get; set; }
    public double StrongDiffRatio { get; set; }
    public double VisualCorrectionPixels { get; set; }

    public double Confidence
    {
        get
        {
            double benchmarkPart = Math.Min(0.55, BenchmarkSamples * 0.055);
            double framePart = Math.Min(0.45, FrameSamples * 0.0125);
            return Math.Clamp(benchmarkPart + framePart, 0, 1);
        }
    }

    public string ConfidenceText => $"{Confidence:P0}";

    public void ObserveBridgeRtt(double milliseconds)
    {
        (BridgeRttMs, BridgeRttVariationMs) = Observe(BridgeRttMs, BridgeRttVariationMs, milliseconds);
        BenchmarkSamples++;
        UpdatedUtc = DateTime.UtcNow;
    }

    public void ObserveBenchmarkSample(double captureMs, double activityMs, double falseQuietMs)
    {
        (CaptureMs, CaptureVariationMs) = Observe(CaptureMs, CaptureVariationMs, captureMs);
        (ActivityMs, ActivityVariationMs) = Observe(ActivityMs, ActivityVariationMs, activityMs);
        (FalseQuietMs, FalseQuietVariationMs) = Observe(FalseQuietMs, FalseQuietVariationMs, falseQuietMs);
        BenchmarkSamples++;
        UpdatedUtc = DateTime.UtcNow;
    }

    public void ObserveFrame(BrowserAgentFrameRecord frame)
    {
        FrameSamples++;
        if (frame.AdaptiveRiskScore == 0) CleanFrames++;
        else if (frame.AdaptiveRiskScore >= 3) RiskyFrames++;

        if (frame.CaptureVisibleTabMs > 0)
        {
            (CaptureMs, CaptureVariationMs) = Observe(CaptureMs, CaptureVariationMs, frame.CaptureVisibleTabMs);
        }
        if (frame.StabilityActivityMs >= 0)
        {
            (ActivityMs, ActivityVariationMs) = Observe(ActivityMs, ActivityVariationMs, frame.StabilityActivityMs);
        }
        if (frame.StabilityMaxFalseQuietMs >= 0)
        {
            (FalseQuietMs, FalseQuietVariationMs) = Observe(FalseQuietMs, FalseQuietVariationMs, frame.StabilityMaxFalseQuietMs);
        }

        RiskScore = Ewma(RiskScore, frame.AdaptiveRiskScore, 0.08);
        if (frame.OverlapVerified)
        {
            OverlapMae = Ewma(OverlapMae, frame.OverlapMeanAbsoluteError, 0.08);
            StrongDiffRatio = Ewma(StrongDiffRatio, frame.OverlapStrongDiffRatio, 0.08);
        }
        VisualCorrectionPixels = Ewma(VisualCorrectionPixels, Math.Abs(frame.VisualDeltaOffsetPixels), 0.08);
        UpdatedUtc = DateTime.UtcNow;
    }

    public BrowserAgentFrameTuning Tune(BrowserAgentFrameTuning baseline)
    {
        if (Confidence < 0.10) return Clone(baseline);

        // RFC 6298-inspired idea: smoothed observations plus variation create a
        // conservative local guard. This is bounded capture tuning, not TCP.
        double falseQuietGuard = FalseQuietMs + 4 * FalseQuietVariationMs;
        double activityGuard = ActivityMs + 2 * ActivityVariationMs;
        double captureGuard = CaptureMs + 2 * CaptureVariationMs;
        double bridgeGuard = BridgeRttMs + 2 * BridgeRttVariationMs;

        int fastSettle = ClampInt(Math.Max(450, falseQuietGuard + 110), 450, 1050);
        double settleScale = baseline.StableWindowMs / 520.0;
        int settle = ClampInt(fastSettle * settleScale, 450, 3000);

        // Start delay is only weakly calibrated. Runtime stability evidence remains
        // authoritative, so a microbenchmark cannot create a huge delay by itself.
        double baseDelayWeight = 0.72 + 0.28 * (1 - Confidence);
        int localIoGuard = ClampInt((bridgeGuard + captureGuard) * 0.18, 0, 260);
        int startDelay = ClampInt(baseline.StartDelayMs * baseDelayWeight + localIoGuard, 0, 1000);
        if (baseline.Gear == 4 && captureGuard <= 500) startDelay = Math.Min(startDelay, 80);

        double overlap = baseline.OverlapRatio;
        if (FrameSamples >= 12)
        {
            double riskPressure = Math.Clamp(RiskScore / 10.0, 0, 0.07);
            double seamPressure = Math.Clamp(OverlapMae / 20.0, 0, 0.05) +
                                  Math.Clamp(StrongDiffRatio * 0.8, 0, 0.05) +
                                  Math.Clamp(VisualCorrectionPixels / 800.0, 0, 0.03);
            overlap += (riskPressure + seamPressure) * Confidence;

            double cleanRatio = CleanFrames / (double)Math.Max(1, FrameSamples);
            if (FrameSamples >= 40 && cleanRatio >= 0.90 && RiskScore < 0.6 && OverlapMae < 0.35)
            {
                overlap -= 0.02 * Confidence;
            }
        }
        overlap = Math.Clamp(overlap, 0.20, 0.50);

        int maxWait = ClampInt(
            Math.Max(baseline.MaxWaitMs, settle * 4.5 + Math.Max(0, activityGuard)),
            3000,
            12000);

        return new BrowserAgentFrameTuning
        {
            Gear = baseline.Gear,
            Name = baseline.Name,
            StartDelayMs = startDelay,
            StableWindowMs = settle,
            MaxWaitMs = maxWait,
            OverlapRatio = overlap
        };
    }

    public string Summary() =>
        $"confidence={Confidence:P0}, samples={BenchmarkSamples} benchmark + {FrameSamples} frames, " +
        $"bridge={BridgeRttMs:F0}±{BridgeRttVariationMs:F0}ms, capture={CaptureMs:F0}±{CaptureVariationMs:F0}ms, " +
        $"activity={ActivityMs:F0}±{ActivityVariationMs:F0}ms, falseQuiet={FalseQuietMs:F0}±{FalseQuietVariationMs:F0}ms";

    public static BrowserAgentCalibrationProfile CreateSyntheticForSelfTest()
    {
        var profile = new BrowserAgentCalibrationProfile();
        for (int i = 0; i < 6; i++) profile.ObserveBridgeRtt(18 + i);
        for (int i = 0; i < 6; i++) profile.ObserveBenchmarkSample(180 + i * 4, 120 + i * 7, 260 + i * 11);
        for (int i = 0; i < 24; i++)
        {
            profile.ObserveFrame(new BrowserAgentFrameRecord
            {
                Sequence = i + 1,
                CaptureVisibleTabMs = 185,
                StabilityActivityMs = 135,
                StabilityMaxFalseQuietMs = 270,
                AdaptiveRiskScore = i % 9 == 0 ? 3 : 0,
                OverlapVerified = true,
                OverlapMeanAbsoluteError = 0.25,
                OverlapStrongDiffRatio = 0.002,
                VisualDeltaOffsetPixels = 1
            });
        }
        return profile;
    }

    private static BrowserAgentFrameTuning Clone(BrowserAgentFrameTuning source) => new()
    {
        Gear = source.Gear,
        Name = source.Name,
        StartDelayMs = source.StartDelayMs,
        StableWindowMs = source.StableWindowMs,
        MaxWaitMs = source.MaxWaitMs,
        OverlapRatio = source.OverlapRatio
    };

    private static (double Smoothed, double Variation) Observe(double smoothed, double variation, double sample)
    {
        if (!double.IsFinite(sample) || sample < 0) return (smoothed, variation);
        if (smoothed <= 0) return (sample, sample / 2.0);

        double old = smoothed;
        double nextVariation = 0.75 * variation + 0.25 * Math.Abs(old - sample);
        double nextSmoothed = 0.875 * old + 0.125 * sample;
        return (nextSmoothed, nextVariation);
    }

    private static double Ewma(double current, double sample, double alpha)
    {
        if (!double.IsFinite(sample)) return current;
        return current == 0 ? sample : (1 - alpha) * current + alpha * sample;
    }

    private static int ClampInt(double value, int min, int max) =>
        Math.Clamp((int)Math.Round(value), min, max);
}

internal static class BrowserAgentCalibrationStore
{
    private static readonly object Gate = new();
    private static BrowserAgentCalibrationProfile? current;

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "BrowserAgentCaptures", "Calibration");
    public static string ProfilePath => Path.Combine(DirectoryPath, "browser-agent-calibration-v016.json");

    public static BrowserAgentCalibrationProfile Current
    {
        get { lock (Gate) return current ??= LoadCore(); }
    }

    public static void Save()
    {
        lock (Gate)
        {
            BrowserAgentCalibrationProfile profile = current ??= LoadCore();
            Directory.CreateDirectory(DirectoryPath);
            string temp = ProfilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ProfilePath, overwrite: true);
        }
    }

    public static BrowserAgentCalibrationProfile Reset()
    {
        lock (Gate)
        {
            current = new BrowserAgentCalibrationProfile();
            try { if (File.Exists(ProfilePath)) File.Delete(ProfilePath); } catch { }
            return current;
        }
    }

    internal static bool PersistenceSelfTest(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "profile.json");
        var profile = BrowserAgentCalibrationProfile.CreateSyntheticForSelfTest();
        File.WriteAllText(path, JsonSerializer.Serialize(profile));
        BrowserAgentCalibrationProfile? restored = JsonSerializer.Deserialize<BrowserAgentCalibrationProfile>(File.ReadAllText(path));
        if (restored is null || restored.BenchmarkSamples != profile.BenchmarkSamples || restored.FrameSamples != profile.FrameSamples) return false;
        File.WriteAllText(path, "{broken-json");
        try
        {
            _ = JsonSerializer.Deserialize<BrowserAgentCalibrationProfile>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return true;
        }
        return false;
    }

    private static BrowserAgentCalibrationProfile LoadCore()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return new BrowserAgentCalibrationProfile();
            BrowserAgentCalibrationProfile? profile = JsonSerializer.Deserialize<BrowserAgentCalibrationProfile>(File.ReadAllText(ProfilePath));
            if (profile is null || profile.SchemaVersion != 1) throw new InvalidDataException("Unsupported calibration profile schema.");
            return profile;
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"[BA_CALIB] ignored invalid calibration profile: {LongCaptureLog.OneLine(ex.Message)}");
            try
            {
                if (File.Exists(ProfilePath))
                {
                    string quarantine = ProfilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Move(ProfilePath, quarantine, overwrite: true);
                }
            }
            catch { }
            return new BrowserAgentCalibrationProfile();
        }
    }
}

internal sealed class BrowserAgentCalibrationResult
{
    public int BridgeSamples { get; init; }
    public int CaptureSamples { get; init; }
    public double Confidence { get; init; }
    public string Summary { get; init; } = string.Empty;
}

internal sealed record BrowserAgentAdaptiveRuntimeSnapshot(
    int Frame,
    string TargetSpeed,
    string CurrentSpeed,
    int StartDelayMs,
    int StableWindowMs,
    int MaxWaitMs,
    double OverlapRatio,
    int ActualSettleMs,
    int ActivityMs,
    int CaptureMs,
    int RiskScore,
    string RiskReasons,
    double CalibrationConfidence,
    int CalibrationSamples);

internal static class BrowserAgentAdaptiveTelemetryHub
{
    private static readonly object Gate = new();
    private static BrowserAgentAdaptiveRuntimeSnapshot? last;

    public static event Action<BrowserAgentAdaptiveRuntimeSnapshot>? Updated;
    public static BrowserAgentAdaptiveRuntimeSnapshot? Last { get { lock (Gate) return last; } }

    public static void Publish(BrowserAgentAdaptiveRuntimeSnapshot snapshot)
    {
        lock (Gate) last = snapshot;
        try { Updated?.Invoke(snapshot); }
        catch (Exception ex) { LongCaptureLog.Warn($"[BA_LIVE] subscriber failed: {LongCaptureLog.OneLine(ex.Message)}"); }
    }

    public static void Reset()
    {
        lock (Gate) last = null;
    }
}
