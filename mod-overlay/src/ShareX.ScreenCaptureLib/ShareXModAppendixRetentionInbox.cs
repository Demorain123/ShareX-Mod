#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRetentionPending(
    string PendingFile,
    string SourceDirectory,
    string[] Files,
    DateTimeOffset Created);

internal static class ShareXModAppendixRetentionInbox
{
    public static ShareXModRetentionPending? FindLatest()
    {
        try
        {
            return ApprovedRoots()
                .Where(Directory.Exists)
                .SelectMany(root => SafeFind(root, "original-retention-pending.json"))
                .Where(File.Exists)
                .OrderByDescending(path =>
                {
                    try { return File.GetLastWriteTimeUtc(path); }
                    catch { return DateTime.MinValue; }
                })
                .Select(TryRead)
                .FirstOrDefault(x => x != null);
        }
        catch
        {
            return null;
        }
    }

    public static bool ResolveKeep(ShareXModRetentionPending pending)
    {
        return Resolve(pending, delete: false, out _);
    }

    public static bool ResolveDelete(
        ShareXModRetentionPending pending,
        out string result)
    {
        return Resolve(pending, delete: true, out result);
    }

    private static bool Resolve(
        ShareXModRetentionPending pending,
        bool delete,
        out string result)
    {
        result = string.Empty;

        try
        {
            if (!File.Exists(pending.PendingFile))
            {
                result = "Pending request no longer exists.";
                return false;
            }

            string source = Path.GetFullPath(pending.SourceDirectory);
            if (!IsInsideApprovedRoot(source))
            {
                result = "Refused: source directory is outside ShareX-Mod capture storage.";
                return false;
            }

            int deleted = 0;
            List<string> refused = new();

            if (delete)
            {
                foreach (string file in pending.Files.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(file)) continue;

                    string candidate = Path.GetFullPath(Path.Combine(source, file));
                    if (!IsSameOrChild(candidate, source) || !IsInsideApprovedRoot(candidate))
                    {
                        refused.Add(file);
                        continue;
                    }

                    try
                    {
                        if (File.Exists(candidate))
                        {
                            File.Delete(candidate);
                            deleted++;
                        }
                    }
                    catch
                    {
                        refused.Add(file);
                    }
                }
            }

            string directory = Path.GetDirectoryName(pending.PendingFile)!;
            string resolvedPath = Path.Combine(
                directory,
                $"original-retention-resolved-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            File.WriteAllText(
                resolvedPath,
                JsonSerializer.Serialize(new
                {
                    format = "ShareX-Mod Original Appendix Retention Resolution",
                    version = "0.7.1-dev",
                    resolved = DateTimeOffset.Now,
                    decision = delete ? "delete" : "keep",
                    sourceDirectory = source,
                    requestedFiles = pending.Files.Length,
                    deletedFiles = deleted,
                    refusedOrFailed = refused
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            File.Delete(pending.PendingFile);

            result = delete
                ? refused.Count == 0
                    ? $"Deleted {deleted} embedded-original file(s)."
                    : $"Deleted {deleted}; kept/refused {refused.Count}."
                : "Original image files kept.";

            return true;
        }
        catch (Exception ex)
        {
            result = ex.Message;
            return false;
        }
    }

    private static ShareXModRetentionPending? TryRead(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            string source = root.TryGetProperty("sourceDirectory", out JsonElement sourceElement)
                ? sourceElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(source) || !IsInsideApprovedRoot(Path.GetFullPath(source)))
            {
                return null;
            }

            List<string> files = new();
            if (root.TryGetProperty("files", out JsonElement fileElement) &&
                fileElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in fileElement.EnumerateArray())
                {
                    string? file = item.GetString();
                    if (!string.IsNullOrWhiteSpace(file)) files.Add(file);
                }
            }

            DateTimeOffset created =
                root.TryGetProperty("created", out JsonElement createdElement) &&
                createdElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(createdElement.GetString(), out DateTimeOffset parsed)
                    ? parsed
                    : new DateTimeOffset(File.GetLastWriteTimeUtc(path));

            return new ShareXModRetentionPending(
                path,
                source,
                files.ToArray(),
                created);
        }
        catch
        {
            return null;
        }
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

    private static IEnumerable<string> ApprovedRoots()
    {
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ShareX-Mod"));
        yield return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShareX-Mod"));
    }

    private static bool IsInsideApprovedRoot(string path)
    {
        string full = Path.GetFullPath(path);
        return ApprovedRoots().Any(root => IsSameOrChild(full, root));
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        string fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));

        if (string.Equals(fullCandidate, fullParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(
            fullParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
