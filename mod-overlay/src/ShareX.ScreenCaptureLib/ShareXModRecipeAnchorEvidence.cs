#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRecipeAnchorEvidence
{
    private const string EndAnchorPrefix = "end-anchor-base64:";
    private const string StartOffsetPrefix = "start-anchor-offset:";
    private const string EndOffsetPrefix = "end-anchor-offset:";

    public static string EncodeEndAnchor(ShareXModRecipeLocator locator)
    {
        string json = JsonSerializer.Serialize(locator);
        return EndAnchorPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static string EncodeStartOffset(double offset) =>
        StartOffsetPrefix + offset.ToString("0.###", CultureInfo.InvariantCulture);

    public static string EncodeEndOffset(double offset) =>
        EndOffsetPrefix + offset.ToString("0.###", CultureInfo.InvariantCulture);

    public static bool TryGetEndAnchor(
        IReadOnlyList<string> evidence,
        out ShareXModRecipeLocator? locator)
    {
        locator = null;
        string? item = evidence.FirstOrDefault(x =>
            x.StartsWith(EndAnchorPrefix, StringComparison.Ordinal));

        if (item == null)
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(item[EndAnchorPrefix.Length..]);
            locator = JsonSerializer.Deserialize<ShareXModRecipeLocator>(
                Encoding.UTF8.GetString(bytes),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return locator != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetStartOffset(
        IReadOnlyList<string> evidence,
        out double offset) =>
        TryGetOffset(evidence, StartOffsetPrefix, out offset);

    public static bool TryGetEndOffset(
        IReadOnlyList<string> evidence,
        out double offset) =>
        TryGetOffset(evidence, EndOffsetPrefix, out offset);

    private static bool TryGetOffset(
        IReadOnlyList<string> evidence,
        string prefix,
        out double offset)
    {
        offset = 0;
        string? item = evidence.FirstOrDefault(x =>
            x.StartsWith(prefix, StringComparison.Ordinal));
        if (item == null)
        {
            return false;
        }

        return double.TryParse(
            item[prefix.Length..],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out offset);
    }
}
