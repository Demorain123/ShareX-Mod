#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModDynamicFeedImageEntry(
    string Snapshot,
    string File,
    string Sha256,
    long Bytes,
    bool DuplicateSkipped);

internal static class ShareXModDynamicFeedImageCollector
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff"
    };

    public static async Task CollectAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string captureDirectory,
        string snapshotLabel)
    {
        if (!settings.ChromeImageAppendixEnabled ||
            !settings.DynamicFeedCollectImages)
        {
            return;
        }

        string safeLabel = Sanitize(snapshotLabel);
        string tempRoot = Path.Combine(
            captureDirectory,
            ".dynamic-image-export",
            safeLabel + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        int originalLimit = settings.ChromeImageAppendixMaxAssets;
        int limit = Math.Clamp(settings.DynamicFeedImageMaxPerSnapshot, 1, 100);

        try
        {
            settings.ChromeImageAppendixMaxAssets = Math.Min(originalLimit, limit);
            await ShareXModChromeImageAppendix.ExportAsync(
                client,
                settings,
                tempRoot);
        }
        catch
        {
            return;
        }
        finally
        {
            settings.ChromeImageAppendixMaxAssets = originalLimit;
        }

        try
        {
            string exported = Path.Combine(tempRoot, "image-appendix");
            if (!Directory.Exists(exported)) return;

            string destination = Path.Combine(captureDirectory, "image-appendix");
            Directory.CreateDirectory(destination);
            ShareXModCaptureSessionContext.RegisterComponent("image-appendix", destination);

            Dictionary<string, string> existing = BuildHashIndex(destination);
            List<ShareXModDynamicFeedImageEntry> entries = new();

            foreach (string source in Directory.GetFiles(exported)
                         .Where(path => Extensions.Contains(Path.GetExtension(path)))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string sha = ComputeSha256(source);
                FileInfo info = new(source);

                if (existing.ContainsKey(sha))
                {
                    entries.Add(new ShareXModDynamicFeedImageEntry(
                        safeLabel,
                        Path.GetFileName(source),
                        sha,
                        info.Length,
                        true));
                    continue;
                }

                string file =
                    $"feed_{safeLabel}_{Sanitize(Path.GetFileNameWithoutExtension(source))}_{sha[..12]}{Path.GetExtension(source).ToLowerInvariant()}";
                string target = Path.Combine(destination, file);
                File.Move(source, target, false);
                existing[sha] = target;

                entries.Add(new ShareXModDynamicFeedImageEntry(
                    safeLabel,
                    file,
                    sha,
                    info.Length,
                    false));
            }

            AppendAudit(destination, entries);
        }
        catch
        {
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    public static bool HasCollection(string captureDirectory) =>
        File.Exists(Path.Combine(
            captureDirectory,
            "image-appendix",
            "dynamic-feed-image-collection.json"));

    private static Dictionary<string, string> BuildHashIndex(string directory)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetFiles(directory)
                     .Where(x => Extensions.Contains(Path.GetExtension(x))))
        {
            try { result[ComputeSha256(path)] = path; }
            catch { }
        }
        return result;
    }

    private static void AppendAudit(
        string directory,
        IReadOnlyList<ShareXModDynamicFeedImageEntry> newEntries)
    {
        string path = Path.Combine(directory, "dynamic-feed-image-collection.json");
        List<ShareXModDynamicFeedImageEntry> all = new();

        try
        {
            if (File.Exists(path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("entries", out JsonElement entries) &&
                    entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in entries.EnumerateArray())
                    {
                        ShareXModDynamicFeedImageEntry? entry =
                            JsonSerializer.Deserialize<ShareXModDynamicFeedImageEntry>(item.GetRawText());
                        if (entry != null) all.Add(entry);
                    }
                }
            }
        }
        catch
        {
            all.Clear();
        }

        all.AddRange(newEntries);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Dynamic Feed Native Image Collection",
                version = "0.10.1-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                updated = DateTimeOffset.Now,
                entryCount = all.Count,
                uniqueSavedCount = all.Count(x => !x.DuplicateSkipped),
                duplicateSkippedCount = all.Count(x => x.DuplicateSkipped),
                entries = all
            }, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "snapshot";
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!(char.IsLetterOrDigit(chars[i]) || chars[i] is '-' or '_')) chars[i] = '_';
        }
        string result = new(chars);
        return result.Length <= 80 ? result : result[..80];
    }
}
