#nullable enable

using System;
using System.IO;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModBuildInfo
{
    private const string FallbackVersion = "0.10.0-dev";
    private static readonly Lazy<string> VersionValue = new(LoadVersion);

    public static string Version => VersionValue.Value;
    public static string DisplayName => "ShareX-Mod " + Version;

    private static string LoadVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.VERSION.txt");
            if (File.Exists(path))
            {
                string value = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch
        {
        }

        return FallbackVersion;
    }
}
