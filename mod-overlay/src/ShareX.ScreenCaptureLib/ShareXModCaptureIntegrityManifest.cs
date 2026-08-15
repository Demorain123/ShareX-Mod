#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModIntegrityFileEntry(
    string RelativePath,
    string Category,
    long Bytes,
    string Sha256);

internal sealed record ShareXModIntegrityVerificationResult(
    bool Valid,
    int CheckedFiles,
    int MissingFiles,
    int MismatchedFiles,
    string Detail);

internal static class ShareXModCaptureIntegrityManifest
{
    private static readonly HashSet<string> CoreExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".json", ".txt"
    };

    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".avif", ".svg"
    };

    public static async Task<string?> TryWriteAsync(
        ShareXModChromeBackgroundCaptureResult result,
        ShareXModV04Settings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.CaptureIntegrityManifestEnabled ||
            result == null ||
            string.IsNullOrWhiteSpace(result.DirectoryPath) ||
            !Directory.Exists(result.DirectoryPath))
        {
            return null;
        }

        try
        {
            return await WriteDirectoryAsync(
                result.DirectoryPath,
                settings.CaptureIntegrityIncludeNativeAssets,
                settings.CaptureIntegrityMaxFiles,
                cancellationToken);
        }
        catch
        {
            // Integrity output is advisory; a valid screenshot must never be discarded because
            // post-capture hashing failed.
            return null;
        }
    }

    internal static async Task<string> WriteDirectoryAsync(
        string captureDirectory,
        bool includeNativeAssets,
        int maxFiles,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(captureDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        int limit = Math.Clamp(maxFiles, 1, 100000);
        string qualityDirectory = Path.Combine(root, "recipe-quality");
        Directory.CreateDirectory(qualityDirectory);
        ShareXModCaptureSessionContext.RegisterComponent("capture-integrity", qualityDirectory);

        string manifestPath = Path.Combine(qualityDirectory, "capture-integrity.json");
        string digestPath = Path.Combine(qualityDirectory, "capture-integrity.sha256");

        List<(string FullPath, string RelativePath, string Category)> allCandidates =
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    string relative = NormalizeRelative(Path.GetRelativePath(root, path));
                    return (FullPath: path, RelativePath: relative, Category: Classify(relative));
                })
                .Where(x => !IsIntegrityOutput(x.RelativePath))
                .Where(x => !IsTransientOrArchive(x.RelativePath))
                .Where(x => IsEligible(x.RelativePath, x.Category, includeNativeAssets))
                .OrderBy(x => x.RelativePath, StringComparer.Ordinal)
                .ToList();

        int candidateFileCount = allCandidates.Count;
        bool truncated = candidateFileCount > limit;
        List<(string FullPath, string RelativePath, string Category)> candidates =
            truncated ? allCandidates.Take(limit).ToList() : allCandidates;

        List<ShareXModIntegrityFileEntry> entries = new(candidates.Count);
        foreach ((string fullPath, string relativePath, string category) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info = new(fullPath);
            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            entries.Add(new ShareXModIntegrityFileEntry(
                relativePath,
                category,
                info.Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }

        string bundleDigest = ComputeBundleDigest(entries);
        ShareXModIntegrityFileEntry? primary = entries.FirstOrDefault(x =>
            x.RelativePath.Equals("capture-full.png", StringComparison.OrdinalIgnoreCase));

        string json = JsonSerializer.Serialize(new
        {
            format = "ShareX-Mod Capture Integrity Manifest",
            version = ShareXModBuildInfo.Version,
            sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
            created = DateTimeOffset.Now,
            algorithm = "SHA-256",
            phase = "post-capture",
            coverage = truncated ? "partial-file-limit" : "complete-selected-output-set",
            includeNativeAssets,
            maxFiles = limit,
            candidateFileCount,
            hashedFileCount = entries.Count,
            bundleDigest,
            primaryOutput = primary == null ? null : new
            {
                primary.RelativePath,
                primary.Bytes,
                primary.Sha256
            },
            note = "SHA-256 verifies saved bytes after capture. Semantic completeness is checked separately by Page Guard / Position Ledger; hash equality alone does not prove that web content was complete.",
            files = entries
        }, new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(
            manifestPath,
            json,
            new UTF8Encoding(false),
            cancellationToken);

        await File.WriteAllTextAsync(
            digestPath,
            bundleDigest + "  capture-integrity-bundle\n",
            new UTF8Encoding(false),
            cancellationToken);

        return manifestPath;
    }

    public static async Task<ShareXModIntegrityVerificationResult> VerifyAsync(
        string captureDirectory,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string root = Path.GetFullPath(captureDirectory);
            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(manifestPath, cancellationToken));

            JsonElement manifest = document.RootElement;
            string expectedBundle = manifest.TryGetProperty("bundleDigest", out JsonElement bundle)
                ? bundle.GetString() ?? string.Empty
                : string.Empty;

            if (!manifest.TryGetProperty("files", out JsonElement files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                return new ShareXModIntegrityVerificationResult(false, 0, 0, 0, "manifest-files-missing");
            }

            List<ShareXModIntegrityFileEntry> expected = new();
            foreach (JsonElement item in files.EnumerateArray())
            {
                string relative = item.GetProperty("RelativePath").GetString() ?? string.Empty;
                string category = item.GetProperty("Category").GetString() ?? string.Empty;
                long bytes = item.GetProperty("Bytes").GetInt64();
                string sha = item.GetProperty("Sha256").GetString() ?? string.Empty;
                expected.Add(new ShareXModIntegrityFileEntry(relative, category, bytes, sha));
            }

            if (!string.Equals(
                    ComputeBundleDigest(expected),
                    expectedBundle,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new ShareXModIntegrityVerificationResult(
                    false,
                    0,
                    0,
                    expected.Count,
                    "manifest-bundle-digest-mismatch");
            }

            int checkedFiles = 0;
            int missing = 0;
            int mismatched = 0;

            foreach (ShareXModIntegrityFileEntry entry in expected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string fullPath = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
                if (!IsInsideRoot(root, fullPath) || !File.Exists(fullPath))
                {
                    missing++;
                    continue;
                }

                FileInfo info = new(fullPath);
                if (info.Length != entry.Bytes)
                {
                    mismatched++;
                    continue;
                }

                await using FileStream stream = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
                string actual = Convert.ToHexString(hash).ToLowerInvariant();
                checkedFiles++;

                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    mismatched++;
                }
            }

            bool valid = missing == 0 && mismatched == 0;
            return new ShareXModIntegrityVerificationResult(
                valid,
                checkedFiles,
                missing,
                mismatched,
                valid ? "verified" : "saved-output-changed-or-missing");
        }
        catch (Exception ex)
        {
            return new ShareXModIntegrityVerificationResult(
                false,
                0,
                0,
                0,
                "verification-error:" + ex.GetType().Name);
        }
    }

    private static bool IsEligible(
        string relativePath,
        string category,
        bool includeNativeAssets)
    {
        string extension = Path.GetExtension(relativePath);
        if (category == "native-original")
        {
            // Collection audit JSON remains part of the core metadata set, but large original
            // image bytes are not hashed twice unless explicitly requested.
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return true;
            return includeNativeAssets &&
                   (CoreExtensions.Contains(extension) || NativeExtensions.Contains(extension));
        }

        return CoreExtensions.Contains(extension) ||
               (includeNativeAssets && NativeExtensions.Contains(extension));
    }

    private static string Classify(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("image-appendix/", StringComparison.OrdinalIgnoreCase))
            return "native-original";
        if (normalized.StartsWith("recipe-quality/", StringComparison.OrdinalIgnoreCase))
            return "quality-metadata";
        if (normalized.Contains("appendix", StringComparison.OrdinalIgnoreCase))
            return "appendix-output";
        if (normalized.EndsWith("capture-full.png", StringComparison.OrdinalIgnoreCase))
            return "primary-output";
        if (normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return "capture-metadata";
        return "capture-part";
    }

    private static bool IsTransientOrArchive(string relativePath)
    {
        string normalized = "/" + relativePath.Replace('\\', '/').Trim('/') + "/";
        return normalized.Contains("/.page-image-export/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.dynamic-image-export/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/page-guard-stale-parts/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/stale-parts/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/replaced-parts/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIntegrityOutput(string relativePath) =>
        relativePath.EndsWith("recipe-quality/capture-integrity.json", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith("recipe-quality/capture-integrity.sha256", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static bool IsInsideRoot(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeBundleDigest(
        IReadOnlyCollection<ShareXModIntegrityFileEntry> entries)
    {
        string canonical = string.Join(
            "\n",
            entries
                .OrderBy(x => x.RelativePath, StringComparer.Ordinal)
                .Select(x => $"{x.RelativePath}\u001f{x.Category}\u001f{x.Bytes}\u001f{x.Sha256}"));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
