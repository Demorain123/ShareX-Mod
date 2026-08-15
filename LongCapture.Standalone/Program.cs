using ShareX.AvaloniaUI.Integration;
using ShareX.ScreenCaptureLib;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace LongCapture.Standalone;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(x => string.Equals(x, "--version", StringComparison.OrdinalIgnoreCase)))
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.VERSION.json")) ? 0 : 2;
        }

        if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunSelfTest();
        }

        InitializeDesktopUiHosts();
        using var form = new MainForm();
        StandaloneUiPolish.Apply(form);
        Application.Run(form);
        return 0;
    }

    private static void InitializeDesktopUiHosts()
    {
        ApplicationConfiguration.Initialize();

        // ShareX's scrolling-capture engine uses an Avalonia overlay window to
        // outline the selected scrolling region. LongCapture owns a WinForms
        // message loop, so explicitly initialize Avalonia in its documented
        // legacy-host mode before any capture can create that overlay.
        AvaloniaBootstrapper.EnsureInitialized();
    }

    private static int RunSelfTest()
    {
        try
        {
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

            // Go beyond constructor-only tests: create a real Win32 target window,
            // inject it into the same ScrollingCaptureManager used after interactive
            // region selection, start a real capture, then exercise the same StopCapture
            // path used by F8. This covers overlay creation, target activation, screen
            // capture, scroll input, result production and cleanup without human input.
            int captureSmoke = RunScrollingCaptureSmokeTest();
            if (captureSmoke != 0) return captureSmoke;

            using (var form = new MainForm())
            {
                StandaloneUiPolish.Apply(form);
                _ = form.Handle;
                if (!form.Text.Contains("LongCapture", StringComparison.OrdinalIgnoreCase)) return 14;
                if (!StandaloneUiPolish.Validate(form, out _)) return 15;
            }

            return 0;
        }
        catch
        {
            return 99;
        }
    }

    private static int RunScrollingCaptureSmokeTest()
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

        Rectangle targetRectangle = target.RectangleToScreen(target.ClientRectangle);
        if (target.Handle == IntPtr.Zero || targetRectangle.IsEmpty) return 17;

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

        FieldInfo? managerField = typeof(ScrollingCaptureService).GetField("_manager", BindingFlags.Instance | BindingFlags.NonPublic);
        object? manager = managerField?.GetValue(service);
        if (manager is null) return 18;

        Type managerType = manager.GetType();
        FieldInfo? selectedWindowField = managerType.GetField("selectedWindow", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? selectedRectangleField = managerType.GetField("selectedRectangle", BindingFlags.Instance | BindingFlags.NonPublic);
        Type? windowInfoType = Type.GetType("ShareX.HelpersLib.WindowInfo, ShareX.HelpersLib", throwOnError: false);
        if (selectedWindowField is null || selectedRectangleField is null || windowInfoType is null) return 19;

        object? windowInfo = Activator.CreateInstance(windowInfoType, target.Handle);
        if (windowInfo is null) return 20;
        selectedWindowField.SetValue(manager, windowInfo);
        selectedRectangleField.SetValue(manager, targetRectangle);

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
