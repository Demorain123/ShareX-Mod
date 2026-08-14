#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModRecipePageIdentity
{
    public static string FromState(ShareXModRecipePageState state) =>
        FromUrl(state.Url);

    public static string FromUrl(string? url)
    {
        string input = url ?? string.Empty;

        try
        {
            Uri uri = new(input);
            // Fragment is client-side viewport/navigation state and should not create a new
            // semantic page identity. Query remains because many paginated sites encode the
            // page/cursor there.
            input = uri.GetLeftPart(UriPartial.Path) + uri.Query;
        }
        catch
        {
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
