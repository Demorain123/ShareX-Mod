#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeJournalPart(
    string File,
    int Step,
    string PageKey,
    double StartY,
    double EndY,
    int Width,
    int Height);

internal sealed record ShareXModRecipeJournalAction(
    int Step,
    string Kind,
    string Status,
    string Detail,
    DateTimeOffset ObservedAt);

internal sealed record ShareXModRecipeJournalSnapshot(
    string Format,
    string Version,
    string? SessionId,
    string RecipePath,
    string RecipeSha256,
    string Mode,
    string OutputDirectory,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    string Status,
    string? InFlightKind,
    int? InFlightStep,
    int LastCompletedStep,
    int NextStep,
    string CurrentPageKey,
    string CurrentUrl,
    double CapturedEndY,
    double KnownDocumentEndY,
    int StepsExecuted,
    int UnchangedPasses,
    IReadOnlyList<ShareXModRecipeJournalPart> Parts,
    IReadOnlyList<ShareXModRecipeJournalAction> Actions,
    string[] Notes);

internal sealed class ShareXModCaptureRecipeRunJournal
{
    private readonly object sync = new();
    private readonly string journalPath;
    private readonly string recipePath;
    private readonly string recipeSha256;
    private readonly string mode;
    private readonly DateTimeOffset created;
    private readonly List<ShareXModRecipeJournalPart> parts = new();
    private readonly List<ShareXModRecipeJournalAction> actions = new();

    private string status = "running";
    private string? inFlightKind;
    private int? inFlightStep;
    private int lastCompletedStep;
    private int nextStep = 1;
    private string currentPageKey = string.Empty;
    private string currentUrl = string.Empty;
    private double capturedEndY;
    private double knownDocumentEndY;
    private int stepsExecuted;
    private int unchangedPasses;

    public string OutputDirectory { get; }
    public string JournalPath => journalPath;
    public bool IsResumed { get; }
    public bool HasInFlightOperation => inFlightStep.HasValue;
    public int NextStep => nextStep;
    public int LastCompletedStep => lastCompletedStep;
    public string CurrentPageKey => currentPageKey;
    public string CurrentUrl => currentUrl;
    public double CapturedEndY => capturedEndY;
    public double KnownDocumentEndY => knownDocumentEndY;
    public int StepsExecuted => stepsExecuted;
    public int UnchangedPasses => unchangedPasses;
    public IReadOnlyList<ShareXModRecipeJournalPart> Parts => parts;
    public IReadOnlyList<ShareXModRecipeJournalAction> Actions => actions;

    private ShareXModCaptureRecipeRunJournal(
        string recipePath,
        string recipeSha256,
        string mode,
        string outputDirectory,
        bool resumed,
        ShareXModRecipeJournalSnapshot? snapshot = null)
    {
        this.recipePath = recipePath;
        this.recipeSha256 = recipeSha256;
        this.mode = mode;
        OutputDirectory = outputDirectory;
        Directory.CreateDirectory(OutputDirectory);
        journalPath = Path.Combine(OutputDirectory, "resume-journal.json");
        IsResumed = resumed;

        if (snapshot == null)
        {
            created = DateTimeOffset.Now;
            WriteAtomic();
            return;
        }

        created = snapshot.Created;
        status = "running";
        inFlightKind = snapshot.InFlightKind;
        inFlightStep = snapshot.InFlightStep;
        lastCompletedStep = snapshot.LastCompletedStep;
        nextStep = Math.Max(1, snapshot.NextStep);
        currentPageKey = snapshot.CurrentPageKey ?? string.Empty;
        currentUrl = snapshot.CurrentUrl ?? string.Empty;
        capturedEndY = snapshot.CapturedEndY;
        knownDocumentEndY = snapshot.KnownDocumentEndY;
        stepsExecuted = snapshot.StepsExecuted;
        unchangedPasses = snapshot.UnchangedPasses;
        parts.AddRange(snapshot.Parts ?? Array.Empty<ShareXModRecipeJournalPart>());
        actions.AddRange(snapshot.Actions ?? Array.Empty<ShareXModRecipeJournalAction>());
        WriteAtomic();
    }

    public static ShareXModCaptureRecipeRunJournal Open(
        ShareXModV04Settings settings,
        string recipePath,
        string mode,
        string suggestedOutputDirectory)
    {
        string fullRecipePath = Path.GetFullPath(recipePath);
        string sha = ComputeSha256(fullRecipePath);

        if (settings.CaptureRecipeResumeEnabled)
        {
            string? requested = ResolveResumeJournal(settings, suggestedOutputDirectory);
            ShareXModRecipeJournalSnapshot? snapshot = TryRead(requested);

            if (snapshot != null &&
                string.Equals(snapshot.RecipeSha256, sha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.Mode, mode, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(snapshot.OutputDirectory) &&
                ValidatePartFiles(snapshot))
            {
                return new ShareXModCaptureRecipeRunJournal(
                    fullRecipePath,
                    sha,
                    mode,
                    snapshot.OutputDirectory,
                    true,
                    snapshot);
            }
        }

        return new ShareXModCaptureRecipeRunJournal(
            fullRecipePath,
            sha,
            mode,
            suggestedOutputDirectory,
            false);
    }

    public void BeginStep(
        int step,
        string kind,
        string pageKey,
        string url)
    {
        lock (sync)
        {
            status = "running";
            inFlightStep = step;
            inFlightKind = kind;
            currentPageKey = pageKey ?? string.Empty;
            currentUrl = url ?? string.Empty;
            WriteAtomic();
        }
    }

    public void CompleteStep(
        int step,
        string pageKey,
        string url)
    {
        lock (sync)
        {
            lastCompletedStep = Math.Max(lastCompletedStep, step);
            nextStep = Math.Max(nextStep, step + 1);
            currentPageKey = pageKey ?? currentPageKey;
            currentUrl = url ?? currentUrl;
            inFlightStep = null;
            inFlightKind = null;
            stepsExecuted++;
            WriteAtomic();
        }
    }

    public void RecordPart(
        string file,
        int step,
        string pageKey,
        double startY,
        double endY,
        int width,
        int height)
    {
        lock (sync)
        {
            ShareXModRecipeJournalPart item = new(
                file,
                step,
                pageKey ?? string.Empty,
                startY,
                endY,
                width,
                height);

            int existing = parts.FindIndex(x =>
                x.Step == step &&
                string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0) parts[existing] = item;
            else parts.Add(item);

            capturedEndY = Math.Max(capturedEndY, endY);
            WriteAtomic();
        }
    }

    public void ReplacePartsForStep(
        int step,
        IEnumerable<ShareXModRecipeJournalPart> replacements)
    {
        lock (sync)
        {
            parts.RemoveAll(x => x.Step == step);
            parts.AddRange(replacements);
            capturedEndY = parts.Count == 0 ? 0 : parts.Max(x => x.EndY);
            WriteAtomic();
        }
    }

    public void RecordAction(
        int step,
        string kind,
        string actionStatus,
        string detail)
    {
        lock (sync)
        {
            actions.Add(new ShareXModRecipeJournalAction(
                step,
                kind,
                actionStatus,
                detail,
                DateTimeOffset.Now));
            WriteAtomic();
        }
    }

    public void RecordFeedProgress(
        double capturedEnd,
        double knownEnd,
        int executedSteps,
        int unchanged)
    {
        lock (sync)
        {
            capturedEndY = Math.Max(capturedEndY, capturedEnd);
            knownDocumentEndY = Math.Max(knownDocumentEndY, knownEnd);
            stepsExecuted = Math.Max(stepsExecuted, executedSteps);
            unchangedPasses = Math.Max(0, unchanged);
            WriteAtomic();
        }
    }

    public void MarkWaitingForReview(string reason)
    {
        lock (sync)
        {
            status = "needs-review";
            actions.Add(new ShareXModRecipeJournalAction(
                inFlightStep ?? nextStep,
                inFlightKind ?? "unknown",
                "needs-review",
                reason,
                DateTimeOffset.Now));
            WriteAtomic();
        }
    }

    public void Complete(string finalStatus, string pageKey, string url)
    {
        lock (sync)
        {
            status = finalStatus;
            currentPageKey = pageKey ?? currentPageKey;
            currentUrl = url ?? currentUrl;
            inFlightStep = null;
            inFlightKind = null;
            WriteAtomic();
        }
    }

    private void WriteAtomic()
    {
        try
        {
            ShareXModRecipeJournalSnapshot snapshot = new(
                "ShareX-Mod Capture Recipe Resume Journal",
                "0.6.6-dev",
                ShareXModCaptureSessionContext.CurrentSessionId,
                recipePath,
                recipeSha256,
                mode,
                OutputDirectory,
                created,
                DateTimeOffset.Now,
                status,
                inFlightKind,
                inFlightStep,
                lastCompletedStep,
                nextStep,
                currentPageKey,
                currentUrl,
                capturedEndY,
                knownDocumentEndY,
                stepsExecuted,
                unchangedPasses,
                parts.OrderBy(x => x.Step).ThenBy(x => x.StartY).ToArray(),
                actions.ToArray(),
                new[]
                {
                    "Resume is accepted only when the Recipe SHA-256 is unchanged and all recorded part files still exist.",
                    "An in-flight navigation/action is never blindly repeated after a crash; it requires page-state reconciliation first."
                });

            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            string temp = journalPath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, journalPath, true);
        }
        catch
        {
            // Journal failure must not invalidate an otherwise valid screenshot.
        }
    }

    private static string? ResolveResumeJournal(
        ShareXModV04Settings settings,
        string suggestedOutputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(settings.CaptureRecipeResumeJournalPath))
        {
            string explicitPath = Path.GetFullPath(settings.CaptureRecipeResumeJournalPath);
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        try
        {
            string root = Path.GetDirectoryName(suggestedOutputDirectory) ?? suggestedOutputDirectory;
            if (!Directory.Exists(root)) return null;

            return Directory.GetFiles(root, "resume-journal.json", SearchOption.AllDirectories)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static ShareXModRecipeJournalSnapshot? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ShareXModRecipeJournalSnapshot>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static bool ValidatePartFiles(ShareXModRecipeJournalSnapshot snapshot)
    {
        try
        {
            foreach (ShareXModRecipeJournalPart part in snapshot.Parts ?? Array.Empty<ShareXModRecipeJournalPart>())
            {
                if (string.IsNullOrWhiteSpace(part.File)) return false;
                string path = Path.IsPathRooted(part.File)
                    ? part.File
                    : Path.Combine(snapshot.OutputDirectory, part.File);
                if (!File.Exists(path)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
