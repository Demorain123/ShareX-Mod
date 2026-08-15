#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal enum ShareXModCaptureMode
{
    Normal = 0,
    RecordRecipe = 1,
    RunRecipe = 2,
    SmartWeb = 3
}

internal sealed record ShareXModCaptureModeProfileData(
    ShareXModCaptureMode Mode,
    string RecipePath,
    DateTimeOffset Updated);

internal static class ShareXModCaptureModeProfile
{
    private static readonly object Sync = new();
    private static ShareXModCaptureModeProfileData? cached;

    public static ShareXModCaptureModeProfileData Current
    {
        get
        {
            lock (Sync)
            {
                cached ??= LoadCore();
                return cached;
            }
        }
    }

    public static ShareXModCaptureModeProfileData SetMode(ShareXModCaptureMode mode)
    {
        lock (Sync)
        {
            ShareXModCaptureModeProfileData current = cached ?? LoadCore();
            string recipe = current.RecipePath;

            if (mode == ShareXModCaptureMode.RunRecipe &&
                (string.IsNullOrWhiteSpace(recipe) || !File.Exists(recipe)))
            {
                recipe = FindLatestRecipe() ?? string.Empty;
            }

            cached = new ShareXModCaptureModeProfileData(
                mode,
                recipe,
                DateTimeOffset.Now);
            SaveCore(cached);
            return cached;
        }
    }

    public static ShareXModCaptureModeProfileData SetRecipePath(string? path)
    {
        lock (Sync)
        {
            ShareXModCaptureModeProfileData current = cached ?? LoadCore();
            string normalized = NormalizeRecipePath(path);
            cached = current with
            {
                RecipePath = normalized,
                Updated = DateTimeOffset.Now
            };
            SaveCore(cached);
            return cached;
        }
    }

    public static ShareXModV04Settings Apply(ShareXModV04Settings settings)
    {
        ShareXModCaptureModeProfileData profile = Current;

        switch (profile.Mode)
        {
            case ShareXModCaptureMode.Normal:
                settings.CaptureRecipeRecordingEnabled = false;
                settings.CaptureRecipeAutomationEnabled = false;
                break;

            case ShareXModCaptureMode.SmartWeb:
                settings.ChromeEnhancedEnabled = true;
                settings.ChromeBackgroundCapture = true;
                settings.CaptureRecipeRecordingEnabled = false;
                settings.CaptureRecipeAutomationEnabled = false;
                break;

            case ShareXModCaptureMode.RecordRecipe:
                settings.ChromeEnhancedEnabled = true;
                settings.CaptureRecipeRecordingEnabled = true;
                settings.CaptureRecipeAutomationEnabled = false;
                break;

            case ShareXModCaptureMode.RunRecipe:
                settings.ChromeEnhancedEnabled = true;
                settings.CaptureRecipeRecordingEnabled = false;
                settings.CaptureRecipeAutomationEnabled = true;

                string recipe = profile.RecipePath;
                if (string.IsNullOrWhiteSpace(recipe) || !File.Exists(recipe))
                {
                    recipe = FindLatestRecipe() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(recipe))
                    {
                        SetRecipePath(recipe);
                    }
                }

                settings.CaptureRecipeReplayPath = recipe;
                break;
        }

        return settings;
    }

    public static string? FindLatestRecipe()
    {
        try
        {
            List<string> roots = new()
            {
                Path.Combine(AppContext.BaseDirectory, "ShareX-Mod"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShareX-Mod")
            };

            return roots
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .SelectMany(root => SafeFind(root, "capture-recipe.json"))
                .Where(File.Exists)
                .OrderByDescending(path =>
                {
                    try { return File.GetLastWriteTimeUtc(path); }
                    catch { return DateTime.MinValue; }
                })
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static string DescribeRecipe()
    {
        string path = Current.RecipePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = FindLatestRecipe() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "No recorded recipe yet";
        }

        string name = Path.GetFileName(Path.GetDirectoryName(path) ?? path);
        DateTime updated;
        try { updated = File.GetLastWriteTime(path); }
        catch { updated = DateTime.MinValue; }

        return updated == DateTime.MinValue
            ? name
            : $"{name} · {updated:g}";
    }

    private static IEnumerable<string> SafeFind(string root, string fileName)
    {
        try
        {
            return Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static ShareXModCaptureModeProfileData LoadCore()
    {
        foreach (string path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;

                ShareXModCaptureModeProfileData? data =
                    JsonSerializer.Deserialize<ShareXModCaptureModeProfileData>(
                        File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data != null)
                {
                    return data with
                    {
                        RecipePath = NormalizeRecipePath(data.RecipePath)
                    };
                }
            }
            catch
            {
            }
        }

        return new ShareXModCaptureModeProfileData(
            ShareXModCaptureMode.Normal,
            string.Empty,
            DateTimeOffset.Now);
    }

    private static void SaveCore(ShareXModCaptureModeProfileData data)
    {
        string json = JsonSerializer.Serialize(
            data,
            new JsonSerializerOptions { WriteIndented = true });

        foreach (string path in CandidatePaths())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, path, true);
                return;
            }
            catch
            {
            }
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "capture-mode.json");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShareX-Mod",
            "capture-mode.json");
    }

    private static string NormalizeRecipePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return path.Trim();
        }
    }
}
