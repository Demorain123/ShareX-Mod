#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModPageLoopApproval(
    string Format,
    string Version,
    string RecipeSha256,
    DateTimeOffset Updated,
    bool Approved,
    int MaxPages);

internal static class ShareXModCaptureRecipePageLoopApproval
{
    public static bool IsApproved(
        string recipePath,
        ShareXModV04Settings settings,
        out int maxPages)
    {
        maxPages = Math.Clamp(settings.CaptureRecipePageLoopMaxPages, 1, 10000);

        if (string.IsNullOrWhiteSpace(recipePath) || !File.Exists(recipePath))
        {
            return false;
        }

        try
        {
            string path = ApprovalPath(recipePath);
            if (!File.Exists(path)) return false;

            ShareXModPageLoopApproval? approval =
                JsonSerializer.Deserialize<ShareXModPageLoopApproval>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (approval == null || !approval.Approved)
            {
                return false;
            }

            string currentSha = ComputeSha256(recipePath);
            if (!string.Equals(
                    currentSha,
                    approval.RecipeSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            maxPages = Math.Clamp(
                approval.MaxPages > 0
                    ? approval.MaxPages
                    : settings.CaptureRecipePageLoopMaxPages,
                1,
                10000);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Save(
        string recipePath,
        bool approved,
        int maxPages)
    {
        if (string.IsNullOrWhiteSpace(recipePath) || !File.Exists(recipePath))
        {
            return false;
        }

        try
        {
            ShareXModPageLoopApproval approval = new(
                "ShareX-Mod Capture Recipe Page Loop Approval",
                "0.8.0-dev",
                ComputeSha256(recipePath),
                DateTimeOffset.Now,
                approved,
                Math.Clamp(maxPages, 1, 10000));

            string path = ApprovalPath(recipePath);
            string temp = path + ".tmp";
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(
                    approval,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temp, path, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static ShareXModPageLoopApproval? Load(string recipePath)
    {
        try
        {
            string path = ApprovalPath(recipePath);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ShareXModPageLoopApproval>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static string ApprovalPath(string recipePath) =>
        Path.Combine(
            Path.GetDirectoryName(recipePath)!,
            "capture-recipe.page-loop.json");

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
