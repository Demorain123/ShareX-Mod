#nullable enable

using System;
using System.IO;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModBuildInfo
{
    private const string FallbackVersion = "0.10.2-dev";
    private static readonly Lazy<string> VersionValue = new(LoadVersion);

    public static string Version => VersionValue.Value;
    public static string DisplayName => "ShareX-Mod " + Version;

    private static string LoadVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ShareX.Mod.VERSION.json");
            if (!File.Exists(path)) return FallbackVersion;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("version", out JsonElement version) &&
                version.ValueKind == JsonValueKind.String)
            {
                string value = version.GetString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch
        {
        }

        return FallbackVersion;
    }
}
