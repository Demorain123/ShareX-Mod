using ShareX.AvaloniaUI.Integration;
using ShareX.ScreenCaptureLib;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        LongCaptureLog.Initialize();
        RegisterGlobalExceptionLogging();
        LongCaptureLog.Info(
            $"process start version={StandaloneVersion.Value} runtime={Environment.Version} os={LongCaptureLog.OneLine(Environment.OSVersion.ToString())} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} baseDir={LongCaptureLog.OneLine(AppContext.BaseDirectory)} args={LongCaptureLog.OneLine(string.Join(' ', args))}");

        if (args.Any(x => string.Equals(x, "--version", StringComparison.OrdinalIgnoreCase)))
        {
            int code = File.Exists(Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.VERSION.json")) ? 0 : 2;
            LongCaptureLog.Info($"--version completed exitCode={code}");
            return code;
        }

        if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = RunSelfTest();
            LongCaptureLog.Info($"--self-test completed exitCode={code}");
            return code;
        }

        if (args.Any(x => string.Equals(x, "--automation-test", StringComparison.OrdinalIgnoreCase)))
        {
            string? reportPath = args
                .FirstOrDefault(x => x.StartsWith("--automation-report=", StringComparison.OrdinalIgnoreCase))?
                .Substring("--automation-report=".Length)
                .Trim('"');
            int code = AutomationTestRunner.Run(reportPath);
            LongCaptureLog.Info($"--automation-test completed exitCode={code}");
            return code;
        }

        InitializeDesktopUiHosts();
        using var exclusionWatcher = CaptureExclusionWatcher.Start();
        using var form = new MainForm();
        StandaloneUiPolish.Apply(form);
        LongCaptureLog.Info($"entering WinForms message loop exclusionWatcher={exclusionWatcher.IsActive}");
        Application.Run(form);
        LongCaptureLog.Info("WinForms message loop exited");
        return 0;
    }

    private static void RegisterGlobalExceptionLogging()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            LongCaptureLog.Error("unhandled WinForms UI-thread exception", e.Exception);
            try
            {
                MessageBox.Show(
                    "LongCapture hit an unexpected UI error. A diagnostic log was saved. Restarting the app is recommended.\n\n" +
                    LongCaptureLog.CurrentLogPath + "\n\n" + e.Exception.Message,
                    "LongCapture unexpected error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // The UI itself may be compromised; the file log is the primary fallback.
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Exception? exception = e.ExceptionObject as Exception;
            LongCaptureLog.Error(
                $"unhandled AppDomain exception terminating={e.IsTerminating} object={LongCaptureLog.OneLine(e.ExceptionObject?.ToString())}",
                exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LongCaptureLog.Error("unobserved task exception", e.Exception);
        };
    }

    internal static void InitializeDesktopUiHosts()
    {
        ApplicationConfiguration.Initialize();

        // ShareX's scrolling-capture engine uses an Avalonia overlay window to
        // outline the selected scrolling region. LongCapture owns a WinForms
        // message loop, so explicitly initialize Avalonia in its documented
        // legacy-host mode before any capture can create that overlay.
        AvaloniaBootstrapper.EnsureInitialized();
        LongCaptureLog.Info("WinForms and Avalonia desktop hosts initialized");
    }

    internal static int RunSelfTest()
    {
        try
        {
            LongCaptureLog.Info("self-test started");
            if (string.IsNullOrWhiteSpace(LongCaptureLog.CurrentLogPath) || !File.Exists(LongCaptureLog.CurrentLogPath)) return 24;

            string versionPath = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.VERSION.json");
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.v04.json");
            if (!File.Exists(versionPath) || !File.Exists(settingsPath)) return 10;

            Type? modBuildInfo = Type.GetType("ShareX.ScreenCaptureLib.ShareXModBuildInfo, ShareX.ScreenCaptureLib", throwOnError: false);
            if (modBuildInfo is null) return 11;

            LongCaptureStandaloneBridge.ConfigureMode(LongCaptureStandaloneMode.SmartWeb);
            LongCaptureStandaloneBridge.ConfigureMode(LongCaptureStandaloneMode.Normal);
            LongCaptureStandaloneReadiness readiness =
                LongCaptureStandaloneBridge.ProbeAsync(LongCaptureStandaloneMode.Normal).GetAwaiter().GetResult();
            if (!readiness.Ready) return 12;

            InitializeDesktopUiHosts();

            using (var service = new ScrollingCaptureService(new ScrollingCaptureOptions
            {
                StartDelay = 100,
                ScrollDelay = 100,
                ScrollMethod = ScrollMethod.MouseWheel,
                ScrollAmount = 1,
                AutoUpload = false,
                ShowRegion = true
            }))
            {
                if (service.IsCapturing) return 13;
            }

            // Regression guard for the real v0.1.1 F8 failure. The standalone
            // host previously constructed the ShareX Avalonia region overlay
            // without initializing Avalonia, so Window.Show() threw
            // "The window has not been initialized." This deliberately executes
            // that exact window bootstrap path inside the packaged executable.
            var overlay = new ScrollingCaptureRegionWindow(new Rectangle(8, 8, 96, 72));
            try
            {
                overlay.Show();
                if (!overlay.IsVisible) return 16;
                overlay.Close();
            }
            finally
            {
                if (overlay.IsVisible)
                {
                    overlay.Close();
                }
            }

            int exclusionSmoke = RunCaptureExclusionSelfTest();
            if (exclusionSmoke != 0) return exclusionSmoke;

            // Go beyond constructor-only tests: create a real Win32 target window,
            // resolve it through the same title/HWND target service used by the GUI,
            // bridge it into ShareX's scrolling manager, start a real capture, then
            // exercise the same StopCapture path used by F8.
            int captureSmoke = RunScrollingCaptureSmokeTest();
            if (captureSmoke != 0) return captureSmoke;

            using (var form = new MainForm())
            {
                StandaloneUiPolish.Apply(form);
                _ = form.Handle;
                if (!form.Text.Contains("LongCapture", StringComparison.OrdinalIgnoreCase)) return 14;
                if (!StandaloneUiPolish.Validate(form, out _)) return 15;
            }

            LongCaptureLog.Info("self-test passed");
            return 0;
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("self-test threw an exception", ex);
            return 99;
        }
    }

    private static int RunCaptureExclusionSelfTest()
    {
        using var background = new Form
        {
            Text = "LongCapture exclusion fixture background",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(520, 120, 360, 260),
            BackColor = Color.White,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow
        };
        background.Show();
        background.Refresh();
        Application.DoEvents();

        Rectangle backgroundClient = background.RectangleToScreen(background.ClientRectangle);
        Rectangle markerBounds = new(
            backgroundClient.Left + 80,
            backgroundClient.Top + 70,
            150,
            90);

        using var sentinel = new Form
        {
            Text = "LongCapture EXCLUSION SENTINEL",
            StartPosition = FormStartPosition.Manual,
            Bounds = markerBounds,
            BackColor = Color.Fuchsia,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            TopMost = true
        };
        sentinel.Controls.Add(new Label
        {
            Text = "LONGCAPTURE-SENTINEL",
            AutoSize = true,
            BackColor = Color.Fuchsia,
            ForeColor = Color.Black,
            Location = new Point(8, 8)
        });
        sentinel.Show();
        sentinel.BringToFront();
        sentinel.Refresh();
        Application.DoEvents();
        Thread.Sleep(120);

        double baselineMarker = CaptureMarkerFraction(markerBounds, Color.Fuchsia);
        CaptureExclusionResult exclusion = CaptureExclusion.Apply(sentinel, "self-test-sentinel");
        if (!exclusion.Applied)
        {
            LongCaptureLog.Warn($"capture exclusion sentinel API failed win32={exclusion.Win32Error}");
            return 25;
        }

        sentinel.Refresh();
        Application.DoEvents();
        Thread.Sleep(180);
        double excludedMarker = CaptureMarkerFraction(markerBounds, Color.Fuchsia);

        LongCaptureLog.Info(
            $"capture exclusion sentinel baselineFraction={baselineMarker:F4} excludedFraction={excludedMarker:F4} verifiedAffinity={(exclusion.VerifiedAffinity.HasValue ? $"0x{exclusion.VerifiedAffinity.Value:X8}" : "unavailable")}");

        // Some non-interactive CI desktops do not expose a real screen surface to
        // Graphics.CopyFromScreen. In that environment we still require the Win32
        // affinity API call itself to succeed, but skip the pixel assertion because
        // there was no observable baseline marker to compare against.
        if (baselineMarker >= 0.20 && excludedMarker >= 0.05)
        {
            LongCaptureLog.Warn("capture exclusion sentinel remained visible in screen-copy validation");
            return 26;
        }

        if (baselineMarker < 0)
        {
            LongCaptureLog.Warn("capture exclusion pixel validation skipped because screen-copy was unavailable in this environment");
        }
        else if (baselineMarker < 0.20)
        {
            LongCaptureLog.Warn("capture exclusion pixel validation skipped because the baseline sentinel was not sufficiently visible");
        }

        sentinel.Close();
        background.Close();
        Application.DoEvents();
        return 0;
    }

    private static double CaptureMarkerFraction(Rectangle bounds, Color marker)
    {
        try
        {
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            long matches = 0;
            long samples = 0;
            for (int y = 2; y < bitmap.Height; y += 4)
            {
                for (int x = 2; x < bitmap.Width; x += 4)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    int distance = Math.Abs(pixel.R - marker.R) + Math.Abs(pixel.G - marker.G) + Math.Abs(pixel.B - marker.B);
                    if (distance <= 30) matches++;
                    samples++;
                }
            }

            return samples == 0 ? 0 : matches / (double)samples;
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"screen-copy marker sampling unavailable type={ex.GetType().Name} message={LongCaptureLog.OneLine(ex.Message)}");
            return -1;
        }
    }

    internal static int RunScrollingCaptureSmokeTest()
    {
        using var target = new Form
        {
            Text = "LongCapture smoke target",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(120, 120, 360, 260),
            BackColor = Color.White,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow
        };
        target.Controls.Add(new Label
        {
            Text = "LongCapture deterministic scrolling-capture smoke target",
            AutoSize = true,
            Location = new Point(18, 18)
        });
        target.Show();
        target.Refresh();
        Application.DoEvents();

        if (target.Handle == IntPtr.Zero) return 17;
        if (!CaptureTargetService.TryCreateTarget(target.Handle, out CaptureTargetDescriptor? captureTarget, out string targetDetail) || captureTarget is null)
        {
            LongCaptureLog.Warn($"self-test target discovery failed detail={LongCaptureLog.OneLine(targetDetail)}");
            return 18;
        }

        Rectangle targetRectangle = captureTarget.Bounds;
        if (targetRectangle.IsEmpty) return 18;

        using var service = new ScrollingCaptureService(new ScrollingCaptureOptions
        {
            StartDelay = 40,
            ScrollDelay = 40,
            ScrollMethod = ScrollMethod.MouseWheel,
            ScrollAmount = 1,
            AutoIgnoreBottomEdge = true,
            AutoUpload = false,
            ShowRegion = true
        });

        if (!ScrollingCaptureTargetBridge.TryAssignTarget(service, captureTarget, out string bridgeDetail))
        {
            LongCaptureLog.Warn($"self-test target bridge failed detail={LongCaptureLog.OneLine(bridgeDetail)}");
            return 19;
        }

        LongCaptureLog.Info($"self-test locked target assigned detail={LongCaptureLog.OneLine(bridgeDetail)}");
        var captureTask = service.StartCaptureAsync();

        // Let at least one frame pass through the real capture pipeline, then request
        // a manual stop exactly as LongCapture's F8 handler does. Do not depend on
        // auto-bottom detection here because ShareX-Mod intentionally applies adaptive
        // settle/boundary logic that can wait longer on a synthetic static window.
        Stopwatch warmup = Stopwatch.StartNew();
        while (!captureTask.IsCompleted && warmup.Elapsed < TimeSpan.FromMilliseconds(900))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        if (!captureTask.IsCompleted)
        {
            service.StopCapture();
        }

        Stopwatch timeout = Stopwatch.StartNew();
        while (!captureTask.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(8))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        if (!captureTask.IsCompleted)
        {
            service.StopCapture();
            return 21;
        }

        ScrollingCaptureStatus status = captureTask.GetAwaiter().GetResult();
        if (status == ScrollingCaptureStatus.Failed || service.Result is null) return 22;
        if (service.Result.Width != targetRectangle.Width || service.Result.Height < targetRectangle.Height) return 23;

        target.Close();
        Application.DoEvents();
        return 0;
    }
}
