#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeExactPart(
    string File,
    int Step,
    string PageKey,
    double StartY,
    double EndY,
    int Width,
    int Height);

internal sealed record ShareXModRecipeExactRangeResult(
    bool Success,
    string Detail,
    IReadOnlyList<ShareXModRecipeExactPart> Parts);

internal static class ShareXModCaptureRecipeExactRange
{
    private sealed record Metrics(double CssWidth, double CssHeight, double DipPerCss);

    public static async Task<ShareXModRecipeExactRangeResult> CaptureAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        ShareXModCaptureRecipeStep effectiveStep,
        string outputDirectory,
        string filePrefix,
        bool includeOverlayOnFirstTile,
        Func<bool>? shouldStop = null)
    {
        if (effectiveStep.Kind != ShareXModCaptureRecipeStepKind.CaptureVerticalRange)
        {
            return new ShareXModRecipeExactRangeResult(
                false,
                "not-a-vertical-range",
                Array.Empty<ShareXModRecipeExactPart>());
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            Metrics metrics = await GetMetricsAsync(client);

            double startY = Math.Clamp(
                effectiveStep.StartY,
                0,
                Math.Max(0, metrics.CssHeight - 1));
            double endY = Math.Clamp(
                effectiveStep.EndY,
                startY + 1,
                metrics.CssHeight);

            int tileHeight = Math.Clamp(
                settings.ChromeBackgroundTileHeight,
                900,
                12000);

            List<ShareXModRecipeExactPart> parts = new();
            double y = startY;

            while (y < endY - 1)
            {
                if (shouldStop?.Invoke() == true)
                {
                    return new ShareXModRecipeExactRangeResult(
                        false,
                        "manual-stop",
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

                if (settings.ChromeRepairRequireStableRegion && !stable.Stable)
                {
                    return new ShareXModRecipeExactRangeResult(
                        false,
                        $"range-unstable:pendingImages={stable.PendingImages};fonts={stable.FontStatus}",
                        parts);
                }

                metrics = await GetMetricsAsync(client);
                double actualEnd = Math.Min(tileEnd, metrics.CssHeight);
                height = actualEnd - y;
                if (height <= 1)
                {
                    break;
                }

                bool hideOverlay =
                    !includeOverlayOnFirstTile ||
                    parts.Count > 0;

                await ShareXModChromeOverlayDeduplicator.SetHiddenAsync(
                    client,
                    hideOverlay);

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
                        return new ShareXModRecipeExactRangeResult(
                            false,
                            "screenshot-empty",
                            parts);
                    }

                    byte[] png = Convert.FromBase64String(base64);
                    string safePrefix = SanitizePrefix(filePrefix);
                    string file = $"{safePrefix}_{parts.Count + 1:D3}.png";
                    string path = Path.Combine(outputDirectory, file);
                    await File.WriteAllBytesAsync(path, png);

                    using MemoryStream stream = new(png, writable: false);
                    using Bitmap bitmap = new(stream);

                    parts.Add(new ShareXModRecipeExactPart(
                        file,
                        effectiveStep.Index,
                        effectiveStep.PageKey,
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
                return new ShareXModRecipeExactRangeResult(
                    false,
                    "no-parts",
                    parts);
            }

            bool coverage =
                Math.Abs(parts[0].StartY - startY) <= 2 &&
                Math.Abs(parts[^1].EndY - endY) <= 2;

            return new ShareXModRecipeExactRangeResult(
                coverage,
                coverage ? "captured" : "coverage-gap",
                parts);
        }
        catch (Exception ex)
        {
            return new ShareXModRecipeExactRangeResult(
                false,
                "range-capture-error:" + ex.GetType().Name,
                Array.Empty<ShareXModRecipeExactPart>());
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

    private static string SanitizePrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "range";

        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!(char.IsLetterOrDigit(chars[i]) || chars[i] is '-' or '_'))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
