#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModPageLoopResumePart(
    string File,
    int PageNumber,
    int TemplateStep,
    string PageKey,
    double StartY,
    double EndY,
    int Width,
    int Height);

internal sealed record ShareXModPageLoopResumeSnapshot(
    string Format,
    string Version,
    string RecipePath,
    string RecipeSha256,
    string OutputDirectory,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    string Status,
    string Phase,
    int CurrentPageNumber,
    int NextPageNumber,
    string CurrentUrl,
    string CurrentFingerprint,
    IReadOnlyList<ShareXModPageLoopResumePart> Parts,
    string[] Notes);

internal sealed class ShareXModPageLoopResumeJournal
{
    private readonly object sync = new();
    private readonly string recipePath;
    private readonly string recipeSha256;
    private readonly string path;
    private readonly DateTimeOffset created;
    private readonly List<ShareXModPageLoopResumePart> parts = new();

    private string status = "running";
    private string phase = "ready";
    private int currentPageNumber;
    private int nextPageNumber = 1;
    private string currentUrl = string.Empty;
    private string currentFingerprint = string.Empty;

    public string OutputDirectory { get; }
    public bool IsResumed { get; }
    public string Phase => phase;
    public int CurrentPageNumber => currentPageNumber;
    public int NextPageNumber => nextPageNumber;
    public string CurrentUrl => currentUrl;
    public string CurrentFingerprint => currentFingerprint;
    public IReadOnlyList<ShareXModPageLoopResumePart> Parts => parts;

    private ShareXModPageLoopResumeJournal(
        string recipePath,
        string recipeSha256,
        string outputDirectory,
        bool resumed,
        ShareXModPageLoopResumeSnapshot? snapshot)
    {
        this.recipePath = recipePath;
        this.recipeSha256 = recipeSha256;
        OutputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);
        path = Path.Combine(outputDirectory, "page-loop-resume.json");
        IsResumed = resumed;

        if (snapshot == null)
        {
            created = DateTimeOffset.Now;
            WriteAtomic();
            return;
        }

        created = snapshot.Created;
        status = "running";
        phase = snapshot.Phase;
        currentPageNumber = snapshot.CurrentPageNumber;
        nextPageNumber = Math.Max(1, snapshot.NextPageNumber);
        currentUrl = snapshot.CurrentUrl ?? string.Empty;
        currentFingerprint = snapshot.CurrentFingerprint ?? string.Empty;
        parts.AddRange(snapshot.Parts ?? Array.Empty<ShareXModPageLoopResumePart>());
        WriteAtomic();
    }

    public static ShareXModPageLoopResumeJournal Open(
        ShareXModV04Settings settings,
        string recipePath,
        string suggestedOutputDirectory)
    {
        string fullRecipe = Path.GetFullPath(recipePath);
        string sha = ComputeSha256(fullRecipe);

        if (settings.CaptureRecipeResumeEnabled)
        {
            ShareXModPageLoopResumeSnapshot? candidate = FindResumeCandidate(
                Path.GetDirectoryName(suggestedOutputDirectory) ?? suggestedOutputDirectory,
                fullRecipe,
                sha);

            if (candidate != null)
            {
                return new ShareXModPageLoopResumeJournal(
                    fullRecipe,
                    sha,
                    candidate.OutputDirectory,
                    true,
                    candidate);
            }
        }

        return new ShareXModPageLoopResumeJournal(
            fullRecipe,
            sha,
            suggestedOutputDirectory,
            false,
            null);
    }

    public void BeginPage(
        int pageNumber,
        ShareXModRecipePageEvidence evidence)
    {
        lock (sync)
        {
            status = "running";
            phase = "capturing";
            currentPageNumber = pageNumber;
            nextPageNumber = pageNumber;
            currentUrl = evidence.Url;
            currentFingerprint = evidence.MainFingerprint;
            WriteAtomic();
        }
    }

    public void BeforeNext(
        int pageNumber,
        ShareXModRecipePageEvidence evidence)
    {
        lock (sync)
        {
            phase = "before-next";
            currentPageNumber = pageNumber;
            nextPageNumber = pageNumber;
            currentUrl = evidence.Url;
            currentFingerprint = evidence.MainFingerprint;
            WriteAtomic();
        }
    }

    public void CompleteTransition(
        int completedPageNumber,
        ShareXModRecipePageEvidence nextPage)
    {
        lock (sync)
        {
            phase = "ready";
            currentPageNumber = completedPageNumber + 1;
            nextPageNumber = completedPageNumber + 1;
            currentUrl = nextPage.Url;
            currentFingerprint = nextPage.MainFingerprint;
            WriteAtomic();
        }
    }

    public void RecordPageParts(
        int pageNumber,
        IEnumerable<ShareXModPageLoopResumePart> pageParts)
    {
        lock (sync)
        {
            parts.RemoveAll(x => x.PageNumber == pageNumber);
            parts.AddRange(pageParts);
            WriteAtomic();
        }
    }

    public void RemovePageParts(int pageNumber, bool archive)
    {
        lock (sync)
        {
            List<ShareXModPageLoopResumePart> old = parts
                .Where(x => x.PageNumber == pageNumber)
                .ToList();

            if (archive)
            {
                string archiveDirectory = Path.Combine(
                    OutputDirectory,
                    "resume-stale-parts");
                Directory.CreateDirectory(archiveDirectory);

                foreach (ShareXModPageLoopResumePart item in old)
                {
                    try
                    {
                        string source = Path.Combine(OutputDirectory, item.File);
                        if (!File.Exists(source)) continue;
                        string target = Path.Combine(
                            archiveDirectory,
                            $"{DateTime.Now:yyyyMMdd-HHmmssfff}_{Path.GetFileName(item.File)}");
                        File.Move(source, target, false);
                    }
                    catch
                    {
                    }
                }
            }

            parts.RemoveAll(x => x.PageNumber == pageNumber);
            WriteAtomic();
        }
    }

    public void NeedsReview(string reason)
    {
        lock (sync)
        {
            status = "needs-review";
            phase = "needs-review";
            WriteAtomic(extraNote: reason);
        }
    }

    public void Complete(string finalStatus)
    {
        lock (sync)
        {
            status = finalStatus;
            phase = "complete";
            WriteAtomic();
        }
    }

    private void WriteAtomic(string? extraNote = null)
    {
        try
        {
            List<string> notes = new()
            {
                "Resume reuses only parts whose files still exist and whose Recipe SHA-256 is unchanged.",
                "A crash during before-next is intentionally ambiguous and requires review instead of blindly clicking Next again."
            };
            if (!string.IsNullOrWhiteSpace(extraNote)) notes.Add(extraNote);

            ShareXModPageLoopResumeSnapshot snapshot = new(
                "ShareX-Mod Page Loop Resume Journal",
                "0.8.2-dev",
                recipePath,
                recipeSha256,
                OutputDirectory,
                created,
                DateTimeOffset.Now,
                status,
                phase,
                currentPageNumber,
                nextPageNumber,
                currentUrl,
                currentFingerprint,
                parts.OrderBy(x => x.PageNumber).ThenBy(x => x.TemplateStep).ThenBy(x => x.StartY).ToArray(),
                notes.ToArray());

            string json = JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions { WriteIndented = true });
            string temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        catch
        {
        }
    }

    private static ShareXModPageLoopResumeSnapshot? FindResumeCandidate(
        string root,
        string recipePath,
        string sha)
    {
        try
        {
            if (!Directory.Exists(root)) return null;

            foreach (string journalPath in Directory.GetFiles(
                         root,
                         "page-loop-resume.json",
                         SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                ShareXModPageLoopResumeSnapshot? snapshot = TryRead(journalPath);
                if (snapshot == null) continue;
                if (snapshot.Status is "complete" or "failed") continue;
                if (!string.Equals(snapshot.RecipePath, recipePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(snapshot.RecipeSha256, sha, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(snapshot.OutputDirectory)) continue;
                if (!AllPartsExist(snapshot)) continue;
                return snapshot;
            }
        }
        catch
        {
        }

        return null;
    }

    private static ShareXModPageLoopResumeSnapshot? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ShareXModPageLoopResumeSnapshot>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static bool AllPartsExist(ShareXModPageLoopResumeSnapshot snapshot)
    {
        foreach (ShareXModPageLoopResumePart part in snapshot.Parts ?? Array.Empty<ShareXModPageLoopResumePart>())
        {
            if (!File.Exists(Path.Combine(snapshot.OutputDirectory, part.File)))
            {
                return false;
            }
        }
        return true;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
