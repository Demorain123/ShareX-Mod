#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModChromeRepairCaptureResult(
    string ManifestPath,
    int CandidateCount,
    ShareXModBrowserCoordinateCalibration Calibration);

internal static class ShareXModChromeRepairCapture
{
    private sealed record RepairRange(long StartY, long EndY, string[] Reasons);

    private sealed record RepairFile(
        string File,
        int SourceRangeIndex,
        long LogicalStartY,
        long LogicalEndY,
        double ClipCssX,
        double ClipCssY,
        double ClipCssWidth,
        double ClipCssHeight,
        double ClipDipX,
        double ClipDipY,
        double ClipDipWidth,
        double ClipDipHeight,
        double ScreenshotScale,
        int ExpectedCropXPixel,
        int ExpectedCropWidthPixel,
        int PixelWidth,
        int PixelHeight,
        ShareXModBrowserStabilityResult Stability,
        string[] Reasons);

    public static async Task<ShareXModChromeRepairCaptureResult?> CaptureCandidatesAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        ShareXModBrowserCoordinateCalibration? calibration)
    {
        if (!settings.ChromeExactRepairCaptureEnabled || calibration == null)
        {
            return null;
        }

        try
        {
            string? repairDirectory =
                ShareXModCaptureSessionContext.TryGetComponentDirectory("repair");

            if (string.IsNullOrWhiteSpace(repairDirectory))
            {
                return null;
            }

            string repairPlanPath = Path.Combine(repairDirectory, "repair-plan.json");
            if (!File.Exists(repairPlanPath))
            {
                return null;
            }

            using JsonDocument plan = JsonDocument.Parse(
                await File.ReadAllTextAsync(repairPlanPath));

            JsonElement root = plan.RootElement;
            if (!root.TryGetProperty("ranges", out JsonElement rangesElement) ||
                rangesElement.ValueKind != JsonValueKind.Array ||
                rangesElement.GetArrayLength() == 0)
            {
                return null;
            }

            long logicalCapturedHeight =
                root.TryGetProperty("logicalCapturedHeight", out JsonElement logicalHeightElement) &&
                logicalHeightElement.TryGetInt64(out long parsedLogicalHeight)
                    ? parsedLogicalHeight
                    : 0;

            List<RepairRange> ranges = new();
            foreach (JsonElement item in rangesElement.EnumerateArray())
            {
                if (!TryReadInt64(item, "StartY", "startY", out long start) ||
                    !TryReadInt64(item, "EndY", "endY", out long end) ||
                    end <= start)
                {
                    continue;
                }

                ranges.Add(new RepairRange(
                    start,
                    end,
                    ReadReasons(item)));
            }

            if (ranges.Count == 0)
            {
                return null;
            }

            string directory = ResolveDirectory();
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("browser-repair", directory);

            double dpr = Math.Max(0.1, calibration.DevicePixelRatio);
            double dipPerCss = calibration.DipPerCss > 0
                ? calibration.DipPerCss
                : 1;

            double screenshotScale = Math.Clamp(dpr / dipPerCss, 0.25, 4.0);

            double selectedCssX = Math.Max(0, calibration.SelectedLeftDocumentCss);
            double selectedCssWidth = Math.Max(1, calibration.SelectedWidthCss);

            double horizontalMarginCss =
                Math.Clamp(settings.ChromeRepairHorizontalMarginCss, 0, 500);

            int maxClipCss =
                Math.Clamp(settings.ChromeRepairMaxClipHeightCss, 800, 12000);

            (double contentWidthCss, double contentHeightCss) =
                await GetContentSizeAsync(client);

            List<RepairFile> files = new();

            for (int rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                RepairRange range = ranges[rangeIndex];
                long cursor = range.StartY;

                while (cursor < range.EndY)
                {
                    long logicalLength = Math.Min(
                        range.EndY - cursor,
                        (long)Math.Max(1, Math.Floor(maxClipCss * dpr)));

                    long logicalEnd = cursor + logicalLength;

                    double cssY =
                        calibration.SelectedTopDocumentCss + cursor / dpr;

                    double cssHeight = logicalLength / dpr;

                    if (cssY < 0)
                    {
                        double clipped = -cssY;
                        cssY = 0;
                        cssHeight = Math.Max(1, cssHeight - clipped);
                    }

                    if (contentHeightCss > 0 && cssY >= contentHeightCss)
                    {
                        break;
                    }

                    if (contentHeightCss > 0)
                    {
                        cssHeight = Math.Min(
                            cssHeight,
                            Math.Max(1, contentHeightCss - cssY));
                    }

                    double clipCssX =
                        Math.Max(0, selectedCssX - horizontalMarginCss);

                    double leftMarginCss =
                        Math.Max(0, selectedCssX - clipCssX);

                    double rightEdgeCss =
                        selectedCssX + selectedCssWidth + horizontalMarginCss;

                    if (contentWidthCss > 0)
                    {
                        rightEdgeCss = Math.Min(rightEdgeCss, contentWidthCss);
                    }

                    double clipCssWidth =
                        Math.Max(1, rightEdgeCss - clipCssX);

                    ShareXModBrowserStabilityResult stability =
                        await ShareXModBrowserStabilityProbe.WaitForRegionAsync(
                            client,
                            settings,
                            cssY,
                            cssHeight);

                    double dipX = clipCssX * dipPerCss;
                    double dipY = cssY * dipPerCss;
                    double dipWidth = clipCssWidth * dipPerCss;
                    double dipHeight = cssHeight * dipPerCss;

                    using JsonDocument response =
                        await client.SendCdpCommandAsync(
                            "Page.captureScreenshot",
                            new
                            {
                                format = "png",
                                fromSurface = true,
                                captureBeyondViewport = true,
                                optimizeForSpeed = true,
                                clip = new
                                {
                                    x = dipX,
                                    y = dipY,
                                    width = dipWidth,
                                    height = dipHeight,
                                    scale = screenshotScale
                                }
                            });

                    string? base64 = response.RootElement
                        .GetProperty("result")
                        .GetProperty("data")
                        .GetString();

                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        byte[] png = Convert.FromBase64String(base64);
                        string fileName =
                            $"repair_{files.Count + 1:D4}_logical_{cursor}_{logicalEnd}.png";

                        string filePath = Path.Combine(directory, fileName);
                        await File.WriteAllBytesAsync(filePath, png);

                        int pixelWidth = 0;
                        int pixelHeight = 0;

                        using (MemoryStream stream = new(png, writable: false))
                        using (Bitmap bitmap = new(stream))
                        {
                            pixelWidth = bitmap.Width;
                            pixelHeight = bitmap.Height;
                        }

                        int expectedCropXPixel =
                            Math.Max(0, (int)Math.Round(leftMarginCss * dpr));

                        int expectedCropWidthPixel =
                            Math.Max(1, (int)Math.Round(selectedCssWidth * dpr));

                        files.Add(new RepairFile(
                            fileName,
                            rangeIndex,
                            cursor,
                            logicalEnd,
                            clipCssX,
                            cssY,
                            clipCssWidth,
                            cssHeight,
                            dipX,
                            dipY,
                            dipWidth,
                            dipHeight,
                            screenshotScale,
                            expectedCropXPixel,
                            expectedCropWidthPixel,
                            pixelWidth,
                            pixelHeight,
                            stability,
                            range.Reasons));
                    }

                    cursor = logicalEnd;
                }
            }

            string manifestPath =
                Path.Combine(directory, "repair-candidates.json");

            string manifest = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Browser Repair Candidates",
                version = "0.5.2-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                created = DateTimeOffset.Now,
                status = files.Count == 0
                    ? "no-candidates-captured"
                    : "alignment-required",
                destructiveReplacementPerformed = false,
                logicalCapturedHeight,
                repairPlanMarginPx = settings.RepairPlanMarginPx,
                coordinateModel = new
                {
                    clipUnit = "device-independent-pixels",
                    screenshotScale,
                    outputPixelsPerCssApprox = dpr,
                    calibration
                },
                candidateCount = files.Count,
                note =
                    "Candidates include a horizontal safety margin. " +
                    "The alignment gate crops/searches this margin and will only " +
                    "replace pixels after top/bottom seam evidence passes.",
                files
            }, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(
                manifestPath,
                manifest,
                new UTF8Encoding(false));

            return new ShareXModChromeRepairCaptureResult(
                manifestPath,
                files.Count,
                calibration);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(double widthCss, double heightCss)> GetContentSizeAsync(
        ShareXModChromeCdpClient client)
    {
        try
        {
            using JsonDocument layout =
                await client.SendCdpCommandAsync("Page.getLayoutMetrics");

            JsonElement result = layout.RootElement.GetProperty("result");
            JsonElement css = result.TryGetProperty(
                "cssContentSize",
                out JsonElement cssSize)
                    ? cssSize
                    : result.GetProperty("contentSize");

            return (
                css.GetProperty("width").GetDouble(),
                css.GetProperty("height").GetDouble());
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool TryReadInt64(
        JsonElement item,
        string first,
        string second,
        out long value)
    {
        value = 0;

        if (item.TryGetProperty(first, out JsonElement a) &&
            a.TryGetInt64(out value))
        {
            return true;
        }

        return item.TryGetProperty(second, out JsonElement b) &&
               b.TryGetInt64(out value);
    }

    private static string[] ReadReasons(JsonElement item)
    {
        JsonElement value;

        if (!item.TryGetProperty("Reasons", out value) &&
            !item.TryGetProperty("reasons", out value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value
            .EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveDirectory()
    {
        string? sessionRoot =
            ShareXModCaptureSessionContext.CurrentRootDirectory;

        if (!string.IsNullOrWhiteSpace(sessionRoot))
        {
            return Path.Combine(sessionRoot, "browser-repair");
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "BrowserRepairs",
            $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");
    }
}
