#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModPaginationIntentDecision(
    bool IsNextPage,
    int Confidence,
    string Reason);

internal static class ShareXModPaginationIntentClassifier
{
    private static readonly HashSet<string> ExactLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "next",
        "next page",
        "go to next page",
        "go next",
        "下一页",
        "下一頁",
        "下页",
        "下頁",
        "下一個頁",
        "下一个页面",
        "下一個頁面",
        "次へ",
        "次のページ",
        "다음",
        "다음 페이지",
        "page suivante",
        "suivant",
        "nächste seite",
        "weiter",
        "página siguiente",
        "pagina siguiente",
        "siguiente"
    };

    private static readonly HashSet<string> ExactIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "next",
        "nextpage",
        "pagenext",
        "paginationnext",
        "pagernext",
        "btnnext",
        "buttonnext"
    };

    public static ShareXModPaginationIntentDecision Classify(ShareXModRecipeLocator locator)
    {
        if (HasRelNext(locator.Rel))
        {
            return new ShareXModPaginationIntentDecision(true, 100, "rel-next");
        }

        string[] labelCandidates =
        {
            locator.AriaLabel,
            locator.Text,
            locator.Name
        };

        bool interactive = IsInteractive(locator);
        foreach (string raw in labelCandidates)
        {
            string label = NormalizeLabel(raw);
            if (label.Length == 0) continue;

            if (ExactLabels.Contains(label) &&
                (interactive || !string.IsNullOrWhiteSpace(locator.AriaLabel)))
            {
                return new ShareXModPaginationIntentDecision(
                    true,
                    90,
                    "exact-next-label:" + label);
            }
        }

        foreach ((string Name, string Value) candidate in new[]
                 {
                     ("id", locator.Id),
                     ("testid", locator.TestId),
                     ("name", locator.Name)
                 })
        {
            string identifier = NormalizeIdentifier(candidate.Value);
            if (identifier.Length > 0 && ExactIdentifiers.Contains(identifier))
            {
                return new ShareXModPaginationIntentDecision(
                    true,
                    82,
                    candidate.Name + "-pagination-next");
            }
        }

        return new ShareXModPaginationIntentDecision(false, 0, "no-high-confidence-next-evidence");
    }

    private static bool HasRelNext(string rel)
    {
        return (rel ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals("next", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInteractive(ShareXModRecipeLocator locator)
    {
        return locator.Tag.Equals("A", StringComparison.OrdinalIgnoreCase) ||
               locator.Tag.Equals("BUTTON", StringComparison.OrdinalIgnoreCase) ||
               locator.Tag.Equals("AREA", StringComparison.OrdinalIgnoreCase) ||
               locator.Tag.Equals("FORM", StringComparison.OrdinalIgnoreCase) ||
               locator.Tag.Equals("SUMMARY", StringComparison.OrdinalIgnoreCase) ||
               locator.Role.Equals("link", StringComparison.OrdinalIgnoreCase) ||
               locator.Role.Equals("button", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        StringBuilder builder = new();
        bool previousSpace = false;
        foreach (char ch in value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousSpace = false;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (!previousSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    previousSpace = true;
                }
            }
            else
            {
                // Punctuation/symbols (ellipsis, arrows, chevrons) do not alter a textual label.
                if (!previousSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    previousSpace = true;
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        StringBuilder builder = new();
        foreach (char ch in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }
}
