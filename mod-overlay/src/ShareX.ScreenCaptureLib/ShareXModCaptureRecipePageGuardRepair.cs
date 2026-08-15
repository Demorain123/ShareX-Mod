#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeGuardReplacementPart(
    string File,
    int Step,
    string PageKey,
    double StartY,
    double EndY,
    int Width,
    int Height);

internal sealed record ShareXModRecipeGuardRepairResult(
    bool Success,
    string Detail,
    IReadOnlyList<ShareXModRecipeGuardReplacementPart> Parts);

internal static class ShareXModCaptureRecipePageGuardRepair
{
    private sealed record Metrics(double CssWidth, double CssHeight, double DipPerCss);

    public static async Task<ShareXModRecipeGuardRepairResult> RecaptureAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        ShareXModRecipeStaleRange stale,
        string outputDirectory,
        Func<bool>? shouldStop = null)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);

            ShareXModCaptureRecipeStep step = stale.EffectiveStep;
            Metrics metrics = await GetMetricsAsync(client);

            double startY = Math.Clamp(step.StartY, 0, Math.Max(0, metrics.CssHeight - 1));
            double endY = Math.Clamp(step.EndY, startY + 1, metrics.CssHeight);
            int tileHeight = Math.Clamp(settings.ChromeBackgroundTileHeight, 900, 12000);
            List<ShareXModRecipeGuardReplacementPart> parts = new();
            double y = startY;

            while (y < endY - 1)
            {
                if (shouldStop?.Invoke() == true)
                {
                    return new ShareXModRecipeGuardRepairResult(
                        false,
                        "manual-stop-during-page-guard-repair",
                        parts);
                }

                double tileEnd = Math.Min(endY, y + tileHeight);
                double height = tileEnd - y;

                ShareXModBrowserStabilityResult stable =
                    await ShareXModBrowserStabilityProbe.WaitForRegionAsync(
                        client,
                        settings,
                        y,
                        height);

                if (!stable.Stable && settings.ChromeRepairRequireStableRegion)
                {
                    return new ShareXModRecipeGuardRepairResult(
                        false,
                        $"range-remained-unstable: pendingImages={stable.PendingImages}, fonts={stable.FontStatus}",
                        parts);
                }

                metrics = await GetMetricsAsync(client);
                double actualEnd = Math.Min(tileEnd, metrics.CssHeight);
                height = actualEnd - y;
                if (height <= 1)
                {
                    break;
                }

                await ShareXModChromeOverlayDeduplicator.SetHiddenAsync(client, true);
                try
                {
                    double dip = metrics.DipPerCss > 0 ? metrics.DipPerCss : 1;
                    using JsonDocument response = await client.SendCdpCommandAsync(
                        "Page.captureScreenshot",
                        new
                        {
                            format = "png",
                            fromSurface = true,
                            captureBeyondViewport = true,
                            optimizeForSpeed = true,
                            clip = new
                            {
                                x = 0d,
                                y = y * dip,
                                width = metrics.CssWidth * dip,
                                height = height * dip,
                                scale = 1d
                            }
                        });

                    string? base64 = response.RootElement
                        .GetProperty("result")
                        .GetProperty("data")
                        .GetString();

                    if (string.IsNullOrWhiteSpace(base64))
                    {
                        return new ShareXModRecipeGuardRepairResult(
                            false,
                            "page-guard-screenshot-empty",
                            parts);
                    }

                    byte[] png = Convert.FromBase64String(base64);
                    string file = $"guard_repair_step_{step.Index:D4}_{parts.Count + 1:D3}_{DateTime.Now:HHmmssfff}.png";
                    string path = Path.Combine(outputDirectory, file);
                    await File.WriteAllBytesAsync(path, png);

                    using MemoryStream stream = new(png, writable: false);
                    using Bitmap bitmap = new(stream);
                    parts.Add(new ShareXModRecipeGuardReplacementPart(
                        file,
                        step.Index,
                        step.PageKey,
                        y,
                        actualEnd,
                        bitmap.Width,
                        bitmap.Height));
                }
                finally
                {
                    await ShareXModChromeOverlayDeduplicator.SetHiddenAsync(client, false);
                }

                y = actualEnd;
            }

            if (parts.Count == 0)
            {
                return new ShareXModRecipeGuardRepairResult(
                    false,
                    "page-guard-produced-no-parts",
                    parts);
            }

            double coveredStart = parts[0].StartY;
            double coveredEnd = parts[^1].EndY;
            bool coverage =
                Math.Abs(coveredStart - startY) <= 2 &&
                Math.Abs(coveredEnd - endY) <= 2;

            return new ShareXModRecipeGuardRepairResult(
                coverage,
                coverage ? "recaptured" : "page-guard-recapture-coverage-gap",
                parts);
        }
        catch (Exception ex)
        {
            return new ShareXModRecipeGuardRepairResult(
                false,
                "page-guard-repair-error:" + ex.GetType().Name,
                Array.Empty<ShareXModRecipeGuardReplacementPart>());
        }
    }

    public static void ArchiveOldPart(
        string outputDirectory,
        string file)
    {
        try
        {
            string source = Path.Combine(outputDirectory, file);
            if (!File.Exists(source)) return;

            string staleDirectory = Path.Combine(outputDirectory, "page-guard-stale-parts");
            Directory.CreateDirectory(staleDirectory);
            string destination = Path.Combine(
                staleDirectory,
                $"{DateTime.Now:yyyyMMdd-HHmmssfff}_{Path.GetFileName(file)}");
            File.Move(source, destination, false);
        }
        catch
        {
            // Keeping an old source part is safer than making capture fail.
        }
    }

    private static async Task<Metrics> GetMetricsAsync(
        ShareXModChromeCdpClient client)
    {
        using JsonDocument layout =
            await client.SendCdpCommandAsync("Page.getLayoutMetrics");

        JsonElement result = layout.RootElement.GetProperty("result");
        JsonElement css = result.TryGetProperty("cssContentSize", out JsonElement cssSize)
            ? cssSize
            : result.GetProperty("contentSize");
        JsonElement dip = result.TryGetProperty("contentSize", out JsonElement dipSize)
            ? dipSize
            : css;

        double cssWidth = css.GetProperty("width").GetDouble();
        double cssHeight = css.GetProperty("height").GetDouble();
        double dipWidth = dip.GetProperty("width").GetDouble();
        double dipPerCss = cssWidth > 0 && dipWidth > 0
            ? dipWidth / cssWidth
            : 1;

        return new Metrics(cssWidth, cssHeight, dipPerCss);
    }
}
