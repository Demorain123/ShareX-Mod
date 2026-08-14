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

internal sealed record ShareXModPageImageCollectionEntry(
    int PageNumber,
    string File,
    string Sha256,
    long Bytes,
    bool DuplicateSkipped);

internal static class ShareXModCaptureRecipePageImageCollector
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    };

    public static async Task CollectAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        string captureDirectory,
        int pageNumber)
    {
        if (!settings.ChromeImageAppendixEnabled ||
            !settings.CaptureRecipePageLoopCollectImages)
        {
            return;
        }

        string tempRoot = Path.Combine(
            captureDirectory,
            ".page-image-export",
            $"page_{pageNumber:D5}_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempRoot);

        int originalLimit = settings.ChromeImageAppendixMaxAssets;
        int pageLimit = Math.Clamp(
            settings.CaptureRecipePageLoopImageMaxPerPage,
            1,
            200);

        try
        {
            settings.ChromeImageAppendixMaxAssets = Math.Min(originalLimit, pageLimit);
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
            if (!Directory.Exists(exported))
            {
                return;
            }

            string destination = Path.Combine(captureDirectory, "image-appendix");
            Directory.CreateDirectory(destination);
            ShareXModCaptureSessionContext.RegisterComponent("image-appendix", destination);

            Dictionary<string, string> existingHashes = BuildExistingHashIndex(destination);
            List<ShareXModPageImageCollectionEntry> newEntries = new();

            foreach (string source in Directory.GetFiles(exported)
                         .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string sha = ComputeSha256(source);
                FileInfo info = new(source);

                if (existingHashes.ContainsKey(sha))
                {
                    newEntries.Add(new ShareXModPageImageCollectionEntry(
                        pageNumber,
                        Path.GetFileName(source),
                        sha,
                        info.Length,
                        true));
                    continue;
                }

                string fileName =
                    $"page_{pageNumber:D5}_{Sanitize(Path.GetFileNameWithoutExtension(source))}_{sha[..12]}{Path.GetExtension(source).ToLowerInvariant()}";
                string target = Path.Combine(destination, fileName);
                File.Move(source, target, false);
                existingHashes[sha] = target;

                newEntries.Add(new ShareXModPageImageCollectionEntry(
                    pageNumber,
                    fileName,
                    sha,
                    info.Length,
                    false));
            }

            AppendAudit(destination, newEntries);
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

    public static bool HasPageLoopCollection(string captureDirectory)
    {
        return File.Exists(Path.Combine(
            captureDirectory,
            "image-appendix",
            "page-loop-image-collection.json"));
    }

    private static Dictionary<string, string> BuildExistingHashIndex(string directory)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.GetFiles(directory)
                     .Where(x => ImageExtensions.Contains(Path.GetExtension(x))))
        {
            try
            {
                result[ComputeSha256(path)] = path;
            }
            catch
            {
            }
        }

        return result;
    }

    private static void AppendAudit(
        string destination,
        IReadOnlyList<ShareXModPageImageCollectionEntry> newEntries)
    {
        string path = Path.Combine(destination, "page-loop-image-collection.json");
        List<ShareXModPageImageCollectionEntry> entries = new();

        try
        {
            if (File.Exists(path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("entries", out JsonElement existing) &&
                    existing.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in existing.EnumerateArray())
                    {
                        ShareXModPageImageCollectionEntry? entry =
                            JsonSerializer.Deserialize<ShareXModPageImageCollectionEntry>(item.GetRawText());
                        if (entry != null) entries.Add(entry);
                    }
                }
            }
        }
        catch
        {
            entries.Clear();
        }

        entries.AddRange(newEntries);

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Page Loop Image Collection",
                version = "0.8.1-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                updated = DateTimeOffset.Now,
                entryCount = entries.Count,
                uniqueSavedCount = entries.Count(x => !x.DuplicateSkipped),
                duplicateSkippedCount = entries.Count(x => x.DuplicateSkipped),
                entries
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
        if (string.IsNullOrWhiteSpace(value)) return "image";
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!(char.IsLetterOrDigit(chars[i]) || chars[i] is '-' or '_'))
            {
                chars[i] = '_';
            }
        }

        string result = new(chars);
        return result.Length <= 80 ? result : result[..80];
    }
}
