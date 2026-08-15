using ShareX.ScreenCaptureLib;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed record AutomationTestCase(
    string Id,
    string Category,
    string Name,
    string Status,
    long DurationMs,
    string Detail);

internal sealed class AutomationTestReport
{
    public string Schema { get; init; } = "longcapture.automation-report.v1";
    public string Version { get; init; } = StandaloneVersion.Value;
    public string Status { get; set; } = "RUNNING";
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }
    public int ManualRequiredCount { get; set; }
    public List<AutomationTestCase> Cases { get; } = new();
}

internal static class AutomationTestRunner
{
    private const int Repetitions = 10;
    private const long MaxManagedGrowthBytes = 64L * 1024L * 1024L;
    private const long MaxPrivateGrowthBytes = 224L * 1024L * 1024L;

    private static readonly string[] SemanticSuites =
    {
        "ShareX.ScreenCaptureLib.ShareXModRecipePlannerSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModRecipeAnchorSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModCaptureIntegritySelfTests",
        "ShareX.ScreenCaptureLib.ShareXModScrollingReliabilitySelfTests",
        "ShareX.ScreenCaptureLib.ShareXModDeferredOverlaySelfTests",
        "ShareX.ScreenCaptureLib.ShareXModTemplateRouterEvidenceSelfTests",
        "ShareX.ScreenCaptureLib.ShareXModPaginationIntentSelfTests"
    };

    public static int Run(string? requestedReportPath)
    {
        var report = new AutomationTestReport();
        string reportPath = ResolveReportPath(requestedReportPath);
        string textPath = Path.ChangeExtension(reportPath, ".txt");

        RunCase(report, "package.integrity", "Package", "Portable package integrity and configuration", TestPackageIntegrity);
        RunCase(report, "core.selftest", "Core", "Packaged shell / F8 / target / Avalonia / UI-exclusion self-test", () =>
        {
            int code = Program.RunSelfTest();
            if (code != 0) throw new InvalidOperationException($"LongCapture core self-test returned exit code {code}.");
            return "Packaged shell self-test returned exit code 0.";
        });
        RunCase(report, "shell.recovery-layout", "Shell", "Target lifecycle recovery and 100-200% layout pressure", LongCaptureRcSelfTests.RunOrThrow);
        RunCase(report, "engine.semantic-regression", "Engine", "Recipe / anchor / integrity / scrolling / fixed / lazy / router / pagination suites", RunSemanticSuites);
        RunCase(report, "diagnostics.roundtrip", "Diagnostics", "Capture-session recorder and diagnostics ZIP round-trip", TestDiagnosticsRoundTrip);
        RunCase(report, "stress.capture-memory", "Stability", $"{Repetitions} consecutive real capture smoke runs and memory growth", RunRepeatedCaptureAndMemoryGate);

        AddManual(report, "manual.live-page-visual", "Real environment", "Real web/app long-image visual inspection", "Deterministic stitch, repeated-pattern, sticky/fixed and lazy-load fixtures are automated; the final pixels on real websites/apps still require visual inspection because page scripts, animation, GPU composition and timing differ by machine/site.");
        AddManual(report, "manual.smart-web-live", "Real environment", "Smart Web live Capture Browser session", "Semantic/router/recipe primitives are regression-tested automatically, but a real signed-in Capture Browser page and site-specific DOM/CDP behavior require a live test.");
        AddManual(report, "manual.daily-chrome-cdp", "Known limitation", "Existing daily Chrome authenticated DOM/CDP reuse", "Normal Long Capture can target an existing Chrome HWND. Reusing that same daily Chrome authenticated DOM/CDP session for Smart Web is not declared complete in v0.1.3 RC2.");
        AddManual(report, "manual.multimonitor-gpu", "Real environment", "Multi-monitor DPI / GPU / animation / infinite-feed behavior", "Automated layout pressure covers 100/125/150/200% font-DPI pressure, but actual monitor transitions, GPU/compositor behavior and unbounded feeds require the real desktop.");
        AddManual(report, "manual.teach-recipe-live", "Real environment", "Teach Capture and Run Recipe live interaction", "Planner, anchor, approval/integrity and pagination intent logic are automated; live browser interaction and final workflow semantics must still be exercised manually.");

        report.PassedCount = report.Cases.Count(x => x.Status == "PASS");
        report.FailedCount = report.Cases.Count(x => x.Status == "FAIL");
        report.ManualRequiredCount = report.Cases.Count(x => x.Status == "MANUAL_REQUIRED");
        report.Status = report.FailedCount == 0 ? "PASS_AUTOMATED" : "FAIL_AUTOMATED";
        report.CompletedAtUtc = DateTime.UtcNow;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(textPath, BuildTextReport(report, reportPath), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("automation report write failed", ex);
            return 91;
        }

        Console.WriteLine(BuildTextReport(report, reportPath));
        LongCaptureLog.Info($"automation acceptance completed status={report.Status} passed={report.PassedCount} failed={report.FailedCount} manual={report.ManualRequiredCount} report={LongCaptureLog.OneLine(reportPath)}");
        return report.FailedCount == 0 ? 0 : 90;
    }

    private static void RunCase(AutomationTestReport report, string id, string category, string name, Func<string> action)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            string detail = action();
            report.Cases.Add(new AutomationTestCase(id, category, name, "PASS", sw.ElapsedMilliseconds, detail));
            Console.WriteLine($"[PASS] {name} - {detail}");
        }
        catch (Exception ex)
        {
            report.Cases.Add(new AutomationTestCase(id, category, name, "FAIL", sw.ElapsedMilliseconds, OneLineException(ex)));
            Console.Error.WriteLine($"[FAIL] {name} - {OneLineException(ex)}");
            LongCaptureLog.Error($"automation case failed id={id} name={name}", ex);
        }
    }

    private static void AddManual(AutomationTestReport report, string id, string category, string name, string detail)
    {
        report.Cases.Add(new AutomationTestCase(id, category, name, "MANUAL_REQUIRED", 0, detail));
    }

    private static string TestPackageIntegrity()
    {
        string root = AppContext.BaseDirectory;
        string[] required =
        {
            "LongCapture.exe",
            "LongCapture.dll",
            "ShareX.ScreenCaptureLib.dll",
            "ShareX.Avalonia.dll",
            "ShareX.Mod.v04.json",
            "ShareX.Mod.VERSION.json",
            "TESTING.md",
            "1-RUN-AUTOMATED-TESTS.cmd",
            "Run-LongCapture-AutomatedTests.ps1"
        };

        foreach (string name in required)
        {
            if (!File.Exists(Path.Combine(root, name))) throw new FileNotFoundException($"Required portable file is missing: {name}");
        }

        if (File.Exists(Path.Combine(root, "ShareX.exe")))
            throw new InvalidOperationException("ShareX.exe is present in the standalone portable package; LongCapture.exe must remain the independent entry point.");

        using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ShareX.Mod.v04.json")));
        using JsonDocument __ = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ShareX.Mod.VERSION.json")));
        return $"Required files/config JSONs valid; ShareX.exe absent; baseDir={root}";
    }

    private static string RunSemanticSuites()
    {
        Assembly assembly = typeof(ScrollingCaptureService).Assembly;
        var passed = new List<string>();
        foreach (string suiteName in SemanticSuites)
        {
            Type type = assembly.GetType(suiteName, throwOnError: true)!;
            MethodInfo method = type.GetMethod("RunOrThrow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(type.FullName, "RunOrThrow");
            object? result = Invoke(method);
            passed.Add(result?.ToString() ?? type.Name);
        }
        return string.Join(" | ", passed);
    }

    private static string TestDiagnosticsRoundTrip()
    {
        string? sessionDir = null;
        string? exported = null;
        string tempExportRoot = Path.Combine(Path.GetTempPath(), "LongCapture-Automation-Diagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new ScrollingCaptureOptions
            {
                StartDelay = 0,
                AutoScrollTop = false,
                ScrollDelay = 50,
                ScrollMethod = ScrollMethod.MouseWheel,
                ScrollAmount = 1,
                AutoIgnoreBottomEdge = true,
                AutoUpload = false,
                ShowRegion = true
            };

            using (var recorder = new CaptureSessionRecorder("AutomationTest", "synthetic diagnostics target", options))
            {
                sessionDir = recorder.SessionDirectory;
                CaptureSessionQuality quality = recorder.Complete(
                    ScrollingCaptureStatus.PartiallySuccessful,
                    savedPath: null,
                    resultSize: null,
                    engineQuality: null);
                if (string.IsNullOrWhiteSpace(quality.Status))
                    throw new InvalidOperationException("Capture session returned an empty quality status.");
            }

            foreach (string file in new[] { "session.json", "quality.json", "session.log", "engine-evidence-unavailable.txt" })
            {
                if (!File.Exists(Path.Combine(sessionDir!, file))) throw new FileNotFoundException($"Diagnostics session missing {file}.");
            }

            exported = CaptureSessionRecorder.ExportLatestBundle(tempExportRoot);
            if (!File.Exists(exported)) throw new FileNotFoundException("Export diagnostics did not create a ZIP.", exported);

            using ZipArchive archive = ZipFile.OpenRead(exported);
            string[] names = archive.Entries.Select(x => x.FullName.Replace('\\', '/')).ToArray();
            foreach (string required in new[] { "session.json", "quality.json", "session.log", "engine-evidence-unavailable.txt" })
            {
                if (!names.Any(x => x.EndsWith(required, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Exported diagnostics ZIP missing {required}.");
            }

            return "Recorder created session/quality/log/evidence files and Export diagnostics produced a readable ZIP containing them.";
        }
        finally
        {
            TryDeleteFile(exported);
            TryDeleteDirectory(tempExportRoot);
            TryDeleteDirectory(sessionDir);
        }
    }

    private static string RunRepeatedCaptureAndMemoryGate()
    {
        long baselineManaged = 0;
        long baselinePrivate = 0;
        long maximumManaged = 0;
        long maximumPrivate = 0;

        for (int iteration = 1; iteration <= Repetitions; iteration++)
        {
            int code = Program.RunScrollingCaptureSmokeTest();
            if (code != 0)
                throw new InvalidOperationException($"Real capture smoke failed on repetition {iteration}/{Repetitions} with exit code {code}.");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long managed = GC.GetTotalMemory(forceFullCollection: true);
            using Process current = Process.GetCurrentProcess();
            current.Refresh();
            long privateBytes = current.PrivateMemorySize64;

            if (iteration == 2)
            {
                baselineManaged = managed;
                baselinePrivate = privateBytes;
                maximumManaged = managed;
                maximumPrivate = privateBytes;
            }
            else if (iteration > 2)
            {
                maximumManaged = Math.Max(maximumManaged, managed);
                maximumPrivate = Math.Max(maximumPrivate, privateBytes);
            }

            Console.WriteLine($"  capture {iteration}/{Repetitions}: managed={FormatMiB(managed)} MiB private={FormatMiB(privateBytes)} MiB");
        }

        if (baselineManaged <= 0 || baselinePrivate <= 0)
            throw new InvalidOperationException("Did not establish a post-warmup memory baseline.");

        long managedGrowth = Math.Max(0, maximumManaged - baselineManaged);
        long privateGrowth = Math.Max(0, maximumPrivate - baselinePrivate);
        if (managedGrowth > MaxManagedGrowthBytes)
            throw new InvalidOperationException($"Managed memory growth {FormatMiB(managedGrowth)} MiB exceeds {FormatMiB(MaxManagedGrowthBytes)} MiB limit.");
        if (privateGrowth > MaxPrivateGrowthBytes)
            throw new InvalidOperationException($"Private memory growth {FormatMiB(privateGrowth)} MiB exceeds {FormatMiB(MaxPrivateGrowthBytes)} MiB limit.");

        return $"{Repetitions} real capture runs passed; managedGrowth={FormatMiB(managedGrowth)} MiB privateGrowth={FormatMiB(privateGrowth)} MiB.";
    }

    private static object? Invoke(MethodInfo method)
    {
        try { return method.Invoke(null, null); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static string ResolveReportPath(string? requested)
    {
        string candidate = requested?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Environment.GetEnvironmentVariable("LONGCAPTURE_AUTOMATION_REPORT")?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(candidate)) return Path.GetFullPath(candidate);

        string root = Path.Combine(AppContext.BaseDirectory, "AutomationReports", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        return Path.Combine(root, "automation-report.json");
    }

    private static string BuildTextReport(AutomationTestReport report, string reportPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LongCapture automated acceptance");
        sb.AppendLine($"Version: {report.Version}");
        sb.AppendLine($"Status: {report.Status}");
        sb.AppendLine($"PASS={report.PassedCount} FAIL={report.FailedCount} MANUAL_REQUIRED={report.ManualRequiredCount}");
        sb.AppendLine($"JSON: {reportPath}");
        sb.AppendLine();
        foreach (AutomationTestCase test in report.Cases)
            sb.AppendLine($"[{test.Status}] {test.Category} / {test.Name} ({test.DurationMs} ms) - {test.Detail}");
        return sb.ToString();
    }

    private static string OneLineException(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}".Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string FormatMiB(long bytes) => (bytes / (1024d * 1024d)).ToString("F1");

    private static void TryDeleteFile(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
