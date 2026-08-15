#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModBrowserCoordinateCalibration(
    double DevicePixelRatio,
    double BrowserScrollX,
    double BrowserScrollY,
    double ContentScreenLeftCss,
    double ContentScreenTopCss,
    double SelectedLeftDocumentCss,
    double SelectedTopDocumentCss,
    double SelectedWidthCss,
    double SelectedHeightCss,
    double DipPerCss,
    string Confidence,
    string[] Notes);

internal sealed record ShareXModChromeSemanticMapResult(
    string Path,
    string Phase,
    double ContentWidthCss,
    double ContentHeightCss,
    ShareXModBrowserCoordinateCalibration Calibration,
    int AnchorCount);

internal static class ShareXModChromeSemanticMap
{
    private sealed record SemanticAnchor(
        int BackendNodeId,
        string Tag,
        double X,
        double Y,
        double Width,
        double Height,
        string Text,
        string Id,
        string ClassName,
        string Role,
        string AriaLabel,
        string Alt,
        string Href,
        string Src,
        string Fingerprint);

    public static async Task<ShareXModChromeSemanticMapResult?> CaptureAsync(
        ShareXModChromeCdpClient client,
        ShareXModChromeTarget target,
        ShareXModV04Settings settings,
        Rectangle selectedRectangle,
        string phase)
    {
        if (!settings.ChromeSemanticMapEnabled)
        {
            return null;
        }

        try
        {
            string directory = ResolveDirectory();
            Directory.CreateDirectory(directory);
            ShareXModCaptureSessionContext.RegisterComponent("semantic-map", directory);

            ShareXModBrowserCoordinateCalibration calibration =
                await MeasureCalibrationAsync(client, selectedRectangle);

            using JsonDocument response = await client.SendCdpCommandAsync(
                "DOMSnapshot.captureSnapshot",
                new
                {
                    computedStyles = Array.Empty<string>(),
                    includePaintOrder = false,
                    includeDOMRects = false,
                    includeBlendedBackgroundColors = false,
                    includeTextColorOpacities = false
                });

            JsonElement result = response.RootElement.GetProperty("result");
            string[] strings = result.GetProperty("strings")
                .EnumerateArray()
                .Select(x => x.GetString() ?? string.Empty)
                .ToArray();

            JsonElement documents = result.GetProperty("documents");
            if (documents.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement document = documents[0];
            JsonElement nodes = document.GetProperty("nodes");
            JsonElement layout = document.GetProperty("layout");

            JsonElement nodeNames = nodes.GetProperty("nodeName");
            JsonElement backendNodeIds = nodes.GetProperty("backendNodeId");
            JsonElement attributes = nodes.GetProperty("attributes");
            JsonElement layoutNodeIndexes = layout.GetProperty("nodeIndex");
            JsonElement bounds = layout.GetProperty("bounds");
            JsonElement layoutText = layout.GetProperty("text");

            int maxAnchors = Math.Clamp(settings.ChromeSemanticMapMaxAnchors, 250, 20000);
            double minWidth = Math.Clamp(settings.ChromeSemanticMapMinWidthCss, 1, 500);
            double minHeight = Math.Clamp(settings.ChromeSemanticMapMinHeightCss, 1, 500);
            int textLimit = Math.Clamp(settings.ChromeSemanticMapTextPreviewChars, 0, 1000);

            List<SemanticAnchor> anchors = new(Math.Min(maxAnchors, 4096));

            int layoutCount = Math.Min(
                layoutNodeIndexes.GetArrayLength(),
                Math.Min(bounds.GetArrayLength(), layoutText.GetArrayLength()));

            for (int layoutIndex = 0; layoutIndex < layoutCount && anchors.Count < maxAnchors; layoutIndex++)
            {
                int nodeIndex = layoutNodeIndexes[layoutIndex].GetInt32();
                if (nodeIndex < 0 ||
                    nodeIndex >= nodeNames.GetArrayLength() ||
                    nodeIndex >= backendNodeIds.GetArrayLength())
                {
                    continue;
                }

                JsonElement rect = bounds[layoutIndex];
                if (rect.ValueKind != JsonValueKind.Array || rect.GetArrayLength() < 4)
                {
                    continue;
                }

                double x = rect[0].GetDouble();
                double y = rect[1].GetDouble();
                double width = rect[2].GetDouble();
                double height = rect[3].GetDouble();

                if (width < minWidth || height < minHeight)
                {
                    continue;
                }

                string tag = GetString(strings, nodeNames[nodeIndex]);
                string text = GetString(strings, layoutText[layoutIndex]);
                text = NormalizeText(text, textLimit);

                Dictionary<string, string> attrs = ReadAttributes(
                    strings,
                    attributes,
                    nodeIndex);

                bool meaningful =
                    IsSemanticTag(tag) ||
                    text.Length > 0 ||
                    attrs.ContainsKey("id") ||
                    attrs.ContainsKey("role") ||
                    attrs.ContainsKey("aria-label") ||
                    attrs.ContainsKey("data-post-number") ||
                    attrs.ContainsKey("data-testid");

                if (!meaningful)
                {
                    continue;
                }

                int backendNodeId = backendNodeIds[nodeIndex].GetInt32();
                string id = GetAttr(attrs, "id");
                string className = Truncate(GetAttr(attrs, "class"), 180);
                string role = Truncate(GetAttr(attrs, "role"), 80);
                string ariaLabel = Truncate(GetAttr(attrs, "aria-label"), 180);
                string alt = Truncate(GetAttr(attrs, "alt"), 180);
                string href = Truncate(GetAttr(attrs, "href"), 320);
                string src = Truncate(
                    FirstNonEmpty(
                        GetAttr(attrs, "src"),
                        GetAttr(attrs, "currentSrc"),
                        GetAttr(attrs, "data-src")),
                    420);

                string fingerprint = Fingerprint(
                    tag,
                    id,
                    role,
                    ariaLabel,
                    alt,
                    href,
                    src,
                    text);

                anchors.Add(new SemanticAnchor(
                    backendNodeId,
                    tag,
                    x,
                    y,
                    width,
                    height,
                    text,
                    id,
                    className,
                    role,
                    ariaLabel,
                    alt,
                    href,
                    src,
                    fingerprint));
            }

            anchors = anchors
                .OrderBy(x => x.Y)
                .ThenBy(x => x.X)
                .ThenByDescending(x => x.Width * x.Height)
                .ToList();

            double contentWidth = TryGetDouble(document, "contentWidth");
            double contentHeight = TryGetDouble(document, "contentHeight");
            double scrollOffsetY = TryGetDouble(document, "scrollOffsetY");

            string fileName = $"semantic-map-{SanitizePhase(phase)}.json";
            string path = Path.Combine(directory, fileName);

            string json = JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod Semantic Capture Map",
                version = "0.5.2-dev",
                sessionId = ShareXModCaptureSessionContext.CurrentSessionId,
                phase,
                created = DateTimeOffset.Now,
                target = new
                {
                    target.Id,
                    target.Title,
                    target.Url
                },
                document = new
                {
                    contentWidthCss = contentWidth,
                    contentHeightCss = contentHeight,
                    snapshotScrollOffsetY = scrollOffsetY
                },
                coordinateCalibration = calibration,
                anchorCount = anchors.Count,
                anchorLimit = maxAnchors,
                anchors
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(path, json, new UTF8Encoding(false));

            return new ShareXModChromeSemanticMapResult(
                path,
                phase,
                contentWidth,
                contentHeight,
                calibration,
                anchors.Count);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ShareXModBrowserCoordinateCalibration> MeasureCalibrationAsync(
        ShareXModChromeCdpClient client,
        Rectangle selectedRectangle)
    {
        using JsonDocument runtime = await client.EvaluateAsync(
            """
(() => ({
  dpr: window.devicePixelRatio || 1,
  scrollX: window.scrollX || 0,
  scrollY: window.scrollY || 0,
  screenX: window.screenX || 0,
  screenY: window.screenY || 0,
  outerWidth: window.outerWidth || 0,
  outerHeight: window.outerHeight || 0,
  innerWidth: window.innerWidth || document.documentElement.clientWidth || 1,
  innerHeight: window.innerHeight || document.documentElement.clientHeight || 1,
  visualScale: window.visualViewport?.scale || 1
}))()
""",
            false);

        JsonElement value = runtime.RootElement
            .GetProperty("result")
            .GetProperty("result")
            .GetProperty("value");

        double dpr = Math.Max(0.1, value.GetProperty("dpr").GetDouble());
        double scrollX = value.GetProperty("scrollX").GetDouble();
        double scrollY = value.GetProperty("scrollY").GetDouble();
        double screenX = value.GetProperty("screenX").GetDouble();
        double screenY = value.GetProperty("screenY").GetDouble();
        double outerWidth = value.GetProperty("outerWidth").GetDouble();
        double outerHeight = value.GetProperty("outerHeight").GetDouble();
        double innerWidth = Math.Max(1, value.GetProperty("innerWidth").GetDouble());
        double innerHeight = Math.Max(1, value.GetProperty("innerHeight").GetDouble());

        double selectedScreenLeftCss = selectedRectangle.Left / dpr;
        double selectedScreenTopCss = selectedRectangle.Top / dpr;
        double selectedWidthCss = selectedRectangle.Width / dpr;
        double selectedHeightCss = selectedRectangle.Height / dpr;

        double horizontalChrome = Math.Max(0, outerWidth - innerWidth);
        double verticalChrome = Math.Max(0, outerHeight - innerHeight);

        double contentScreenLeftCss = screenX + horizontalChrome * 0.5;
        double contentScreenTopCss = screenY + verticalChrome;

        double selectedLeftDocumentCss =
            scrollX + selectedScreenLeftCss - contentScreenLeftCss;
        double selectedTopDocumentCss =
            scrollY + selectedScreenTopCss - contentScreenTopCss;

        double dipPerCss = 1;
        try
        {
            using JsonDocument layout =
                await client.SendCdpCommandAsync("Page.getLayoutMetrics");

            JsonElement layoutResult = layout.RootElement.GetProperty("result");
            if (layoutResult.TryGetProperty("cssContentSize", out JsonElement cssSize) &&
                layoutResult.TryGetProperty("contentSize", out JsonElement dipSize))
            {
                double cssWidth = cssSize.GetProperty("width").GetDouble();
                double dipWidth = dipSize.GetProperty("width").GetDouble();
                if (cssWidth > 0 && dipWidth > 0)
                {
                    dipPerCss = dipWidth / cssWidth;
                }
            }
        }
        catch
        {
        }

        List<string> notes = new();
        string confidence = "estimated";

        if (selectedRectangle.Width <= 0 || selectedRectangle.Height <= 0)
        {
            confidence = "unavailable";
            notes.Add("selected capture rectangle is empty");
        }
        else
        {
            if (selectedTopDocumentCss < -64)
            {
                notes.Add("selected region appears above the browser content viewport");
            }

            double expectedViewportPhysical = innerWidth * dpr;
            double widthRatio = selectedRectangle.Width / Math.Max(1, expectedViewportPhysical);

            if (widthRatio >= 0.75 && widthRatio <= 1.08)
            {
                notes.Add("selected width is close to browser viewport width");
            }
            else
            {
                notes.Add("selected region is a partial-width browser capture");
            }

            notes.Add(
                "mapping uses Win32 screen pixels divided by devicePixelRatio; " +
                "repair images are candidates until pixel alignment verifies them");
        }

        return new ShareXModBrowserCoordinateCalibration(
            dpr,
            scrollX,
            scrollY,
            contentScreenLeftCss,
            contentScreenTopCss,
            selectedLeftDocumentCss,
            selectedTopDocumentCss,
            selectedWidthCss,
            selectedHeightCss,
            dipPerCss > 0 ? dipPerCss : 1,
            confidence,
            notes.ToArray());
    }

    private static Dictionary<string, string> ReadAttributes(
        string[] strings,
        JsonElement attributes,
        int nodeIndex)
    {
        Dictionary<string, string> result =
            new(StringComparer.OrdinalIgnoreCase);

        if (nodeIndex < 0 ||
            nodeIndex >= attributes.GetArrayLength())
        {
            return result;
        }

        JsonElement item = attributes[nodeIndex];
        if (item.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        for (int i = 0; i + 1 < item.GetArrayLength(); i += 2)
        {
            string name = GetString(strings, item[i]);
            string value = GetString(strings, item[i + 1]);

            if (name.Length > 0)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static string GetString(string[] strings, JsonElement indexElement)
    {
        if (indexElement.ValueKind != JsonValueKind.Number ||
            !indexElement.TryGetInt32(out int index) ||
            index < 0 ||
            index >= strings.Length)
        {
            return string.Empty;
        }

        return strings[index] ?? string.Empty;
    }

    private static double TryGetDouble(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static bool IsSemanticTag(string tag)
    {
        return tag.Equals("IMG", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("VIDEO", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("ARTICLE", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("SECTION", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("P", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("LI", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("BLOCKQUOTE", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("TABLE", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("FIGURE", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("FIGCAPTION", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("DETAILS", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("SUMMARY", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("H1", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("H2", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("H3", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("H4", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("H5", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("H6", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("A", StringComparison.OrdinalIgnoreCase) ||
               tag.Equals("BUTTON", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string value, int limit)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return Truncate(normalized, limit);
    }

    private static string Fingerprint(params string[] parts)
    {
        string joined = string.Join(
            "\u001f",
            parts.Select(x => x?.Trim() ?? string.Empty));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string GetAttr(
        Dictionary<string, string> attrs,
        string name)
    {
        return attrs.TryGetValue(name, out string? value)
            ? value ?? string.Empty
            : string.Empty;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || max <= 0)
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    private static string SanitizePhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return "snapshot";
        }

        StringBuilder builder = new();
        foreach (char c in phase)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_'
                ? char.ToLowerInvariant(c)
                : '-');
        }

        return builder.ToString().Trim('-');
    }

    private static string ResolveDirectory()
    {
        string? sessionRoot = ShareXModCaptureSessionContext.CurrentRootDirectory;
        if (!string.IsNullOrWhiteSpace(sessionRoot))
        {
            return Path.Combine(sessionRoot, "semantic");
        }

        string fallback = Path.Combine(
            AppContext.BaseDirectory,
            "ShareX-Mod",
            "SemanticMaps",
            $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}");

        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
