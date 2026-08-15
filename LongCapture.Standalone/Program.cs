using ShareX.ScreenCaptureLib;
using System;
using System.IO;
using System.Linq;
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

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
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

            using (var service = new ScrollingCaptureService(new ScrollingCaptureOptions
            {
                StartDelay = 100,
                ScrollDelay = 100,
                ScrollMethod = ScrollMethod.MouseWheel,
                ScrollAmount = 1,
                AutoUpload = false,
                ShowRegion = false
            }))
            {
                if (service.IsCapturing) return 13;
            }

            ApplicationConfiguration.Initialize();
            using (var form = new MainForm())
            {
                _ = form.Handle;
                if (!form.Text.Contains("LongCapture", StringComparison.OrdinalIgnoreCase)) return 14;
            }

            return 0;
        }
        catch
        {
            return 99;
        }
    }
}
