#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRecipeTemplateFamily(
    string Id,
    string RepresentativePageKey,
    IReadOnlyList<ShareXModCaptureRecipeStep> Steps,
    int DemonstratedPages,
    double InternalSimilarity);

internal sealed record ShareXModRecipeTemplateRouterPlan(
    bool Candidate,
    string Reason,
    string InitialPageKey,
    IReadOnlyList<ShareXModCaptureRecipeStep> InitialSteps,
    IReadOnlyList<ShareXModRecipeTemplateFamily> Families,
    int DemonstratedRepeatablePages);

internal sealed record ShareXModRecipeLocatorProbe(
    bool Resolved,
    double Score,
    double Gap,
    string Detail);

internal sealed record ShareXModRecipeTemplateSelection(
    bool Selected,
    string Status,
    ShareXModRecipeTemplateFamily? Family,
    double Score,
    double Gap,
    IReadOnlyDictionary<string, double> FamilyScores,
    string[] Evidence);

internal static class ShareXModCaptureRecipeTemplateRouter
{
    private sealed record PagePattern(
        string PageKey,
        IReadOnlyList<ShareXModCaptureRecipeStep> Steps);

    private sealed class FamilyBuilder
    {
        public string Id { get; }
        public PagePattern Representative { get; }
        public List<PagePattern> Members { get; } = new();
        public List<double> Similarities { get; } = new();

        public FamilyBuilder(string id, PagePattern representative)
        {
            Id = id;
            Representative = representative;
            Members.Add(representative);
            Similarities.Add(1);
        }

        public double InternalSimilarity => Similarities.Count == 0
            ? 1
            : Similarities.Average();
    }

    public static ShareXModRecipeTemplateRouterPlan Build(
        ShareXModCaptureRecipe recipe,
        ShareXModV04Settings settings)
    {
        if (!settings.CaptureRecipeTemplateRouterEnabled)
        {
            return Reject("template-router-disabled");
        }

        List<PagePattern> repeatable = recipe.Steps
            .Where(x => !string.IsNullOrWhiteSpace(x.PageKey))
            .OrderBy(x => x.Index)
            .GroupBy(x => x.PageKey)
            .Select(g => BuildPattern(g.Key, g.OrderBy(x => x.Index).ToList()))
            .Where(IsRepeatable)
            .ToList();

        // Two demonstrated pages are already handled by the simpler adaptive template. A router
        // becomes useful only when there is enough evidence for at least two recurrent families.
        if (repeatable.Count < 3)
        {
            return Reject("need-three-demonstrated-repeatable-pages");
        }

        PagePattern initial = repeatable[0];
        double threshold = Math.Clamp(
            settings.CaptureRecipeTemplateRouterMinSimilarity,
            0.5,
            1.0);
        int maxFamilies = Math.Clamp(
            settings.CaptureRecipeTemplateRouterMaxFamilies,
            2,
            32);

        List<FamilyBuilder> families = new();

        foreach (PagePattern page in repeatable.Skip(1))
        {
            FamilyBuilder? best = null;
            double bestScore = -1;

            foreach (FamilyBuilder family in families)
            {
                double score = PatternSimilarity(family.Representative, page);
                if (score > bestScore)
                {
                    best = family;
                    bestScore = score;
                }
            }

            if (best != null && bestScore >= threshold)
            {
                best.Members.Add(page);
                best.Similarities.Add(bestScore);
                continue;
            }

            if (families.Count >= maxFamilies)
            {
                return Reject("template-family-safety-limit");
            }

            families.Add(new FamilyBuilder(
                $"family-{families.Count + 1:D2}",
                page));
        }

        if (families.Count < 2)
        {
            return Reject("single-family-use-adaptive-template");
        }

        ShareXModRecipeTemplateFamily[] result = families
            .Select(f => new ShareXModRecipeTemplateFamily(
                f.Id,
                f.Representative.PageKey,
                f.Representative.Steps,
                f.Members.Count,
                f.InternalSimilarity))
            .ToArray();

        return new ShareXModRecipeTemplateRouterPlan(
            true,
            $"multi-family-semantic-router:{result.Length}",
            initial.PageKey,
            initial.Steps,
            result,
            repeatable.Count);
    }

    public static async Task<ShareXModRecipeTemplateSelection> SelectAsync(
        ShareXModChromeCdpClient client,
        ShareXModRecipeTemplateRouterPlan plan,
        ShareXModV04Settings settings)
    {
        if (!plan.Candidate || plan.Families.Count == 0)
        {
            return new ShareXModRecipeTemplateSelection(
                false,
                "router-not-candidate",
                null,
                0,
                0,
                new Dictionary<string, double>(),
                Array.Empty<string>());
        }

        Dictionary<string, double> familyScores = new(StringComparer.Ordinal);
        Dictionary<string, string[]> evidence = new(StringComparer.Ordinal);
        List<(ShareXModRecipeTemplateFamily Family, double Score)> valid = new();

        foreach (ShareXModRecipeTemplateFamily family in plan.Families)
        {
            List<ShareXModCaptureRecipeStep> locatorSteps = family.Steps
                .Where(x => x.Locator != null &&
                            x.Kind is ShareXModCaptureRecipeStepKind.ExpandOrActivate or
                                      ShareXModCaptureRecipeStepKind.HorizontalSweep or
                                      ShareXModCaptureRecipeStepKind.NextPage)
                .ToList();

            if (locatorSteps.Count == 0)
            {
                familyScores[family.Id] = 0;
                evidence[family.Id] = new[] { "no-semantic-locators" };
                continue;
            }

            double sum = 0;
            int weighted = 0;
            bool invalid = false;
            List<string> familyEvidence = new();

            foreach (ShareXModCaptureRecipeStep step in locatorSteps)
            {
                ShareXModRecipeLocatorProbe probe =
                    await ProbeLocatorAsync(client, step.Locator!);

                bool strong = probe.Resolved && probe.Score >= 80 && probe.Gap >= 20;
                bool usable = probe.Resolved && probe.Score >= 48 && probe.Gap >= 8;
                double stepScore = strong ? 1.0 : usable ? 0.72 : 0;

                familyEvidence.Add(
                    $"step-{step.Index}:{step.Kind}:{probe.Detail}:score={probe.Score:0.0}:gap={probe.Gap:0.0}");

                if (!usable && step.Required)
                {
                    invalid = true;
                    familyEvidence.Add($"required-step-{step.Index}-unresolved");
                    break;
                }

                // Required semantic actions carry more weight. Optional actions can help select a
                // family but can never rescue a missing required locator.
                int weight = step.Required ? 3 : 1;
                sum += stepScore * weight;
                weighted += weight;
            }

            double score = invalid || weighted == 0 ? 0 : sum / weighted;
            familyScores[family.Id] = score;
            evidence[family.Id] = familyEvidence.ToArray();
            if (!invalid && score > 0)
            {
                valid.Add((family, score));
            }
        }

        if (valid.Count == 0)
        {
            return new ShareXModRecipeTemplateSelection(
                false,
                "no-family-resolved",
                null,
                0,
                0,
                familyScores,
                evidence.Values.SelectMany(x => x).ToArray());
        }

        valid.Sort((a, b) => b.Score.CompareTo(a.Score));
        (ShareXModRecipeTemplateFamily bestFamily, double bestScore) = valid[0];
        double second = valid.Count > 1 ? valid[1].Score : 0;
        double gap = bestScore - second;
        double minScore = Math.Clamp(
            settings.CaptureRecipeTemplateRouterMinSelectionScore,
            0.25,
            1.0);
        double minGap = Math.Clamp(
            settings.CaptureRecipeTemplateRouterMinUniquenessGap,
            0.01,
            0.5);

        bool selected = bestScore >= minScore &&
                        (valid.Count == 1 || gap >= minGap);

        return new ShareXModRecipeTemplateSelection(
            selected,
            selected ? "selected" : "ambiguous-family",
            selected ? bestFamily : null,
            bestScore,
            gap,
            familyScores,
            evidence.TryGetValue(bestFamily.Id, out string[]? selectedEvidence)
                ? selectedEvidence
                : Array.Empty<string>());
    }

    private static PagePattern BuildPattern(
        string pageKey,
        List<ShareXModCaptureRecipeStep> steps)
    {
        return new PagePattern(
            pageKey,
            steps
                .Where(x => x.Kind != ShareXModCaptureRecipeStepKind.PageCheckpoint &&
                            x.Kind != ShareXModCaptureRecipeStepKind.WaitStable)
                .ToList());
    }

    private static bool IsRepeatable(PagePattern pattern) =>
        pattern.Steps.Any(x => x.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange) &&
        pattern.Steps.Any(x => x.Kind == ShareXModCaptureRecipeStepKind.NextPage && x.Locator != null);

    private static double PatternSimilarity(PagePattern a, PagePattern b)
    {
        if (a.Steps.Count == 0 || b.Steps.Count == 0) return 0;

        int max = Math.Max(a.Steps.Count, b.Steps.Count);
        int min = Math.Min(a.Steps.Count, b.Steps.Count);
        double countScore = min / (double)max;
        double sum = 0;

        for (int i = 0; i < min; i++)
        {
            ShareXModCaptureRecipeStep left = a.Steps[i];
            ShareXModCaptureRecipeStep right = b.Steps[i];
            if (left.Kind != right.Kind) continue;

            double locator = LocatorSimilarity(left.Locator, right.Locator);
            double range = left.Kind == ShareXModCaptureRecipeStepKind.CaptureVerticalRange
                ? RangeShapeSimilarity(left, right)
                : 1;

            sum += 0.55 + locator * 0.30 + range * 0.15;
        }

        return Math.Clamp((sum / max) * 0.85 + countScore * 0.15, 0, 1);
    }

    private static double LocatorSimilarity(
        ShareXModRecipeLocator? a,
        ShareXModRecipeLocator? b)
    {
        if (a == null && b == null) return 1;
        if (a == null || b == null) return 0;

        double score = 0;
        double weight = 0;
        Compare(a.Id, b.Id, 5, ref score, ref weight);
        Compare(a.TestId, b.TestId, 5, ref score, ref weight);
        Compare(a.AriaLabel, b.AriaLabel, 4, ref score, ref weight);
        Compare(a.Role, b.Role, 3, ref score, ref weight);
        Compare(a.Name, b.Name, 2, ref score, ref weight);
        Compare(a.Tag, b.Tag, 2, ref score, ref weight);
        Compare(a.Href, b.Href, 1, ref score, ref weight, allowSameHost: true);
        Compare(a.Text, b.Text, 3, ref score, ref weight, fuzzy: true);
        return weight <= 0 ? 0.5 : Math.Clamp(score / weight, 0, 1);
    }

    private static double RangeShapeSimilarity(
        ShareXModCaptureRecipeStep a,
        ShareXModCaptureRecipeStep b)
    {
        double ah = Math.Max(1, a.EndY - a.StartY);
        double bh = Math.Max(1, b.EndY - b.StartY);
        return Math.Min(ah, bh) / Math.Max(ah, bh);
    }

    private static void Compare(
        string? a,
        string? b,
        double weightValue,
        ref double score,
        ref double weight,
        bool fuzzy = false,
        bool allowSameHost = false)
    {
        string left = Normalize(a);
        string right = Normalize(b);
        if (left.Length == 0 && right.Length == 0) return;
        weight += weightValue;
        if (left.Length == 0 || right.Length == 0) return;

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            score += weightValue;
            return;
        }

        if (allowSameHost &&
            Uri.TryCreate(left, UriKind.Absolute, out Uri? lu) &&
            Uri.TryCreate(right, UriKind.Absolute, out Uri? ru) &&
            string.Equals(lu.Host, ru.Host, StringComparison.OrdinalIgnoreCase))
        {
            score += weightValue * 0.45;
            return;
        }

        if (fuzzy &&
            (left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
             right.Contains(left, StringComparison.OrdinalIgnoreCase)))
        {
            score += weightValue * 0.55;
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static async Task<ShareXModRecipeLocatorProbe> ProbeLocatorAsync(
        ShareXModChromeCdpClient client,
        ShareXModRecipeLocator locator)
    {
        string locatorJson = JsonSerializer.Serialize(locator);
        string script = $$"""
(() => {
  const locator = {{locatorJson}};
  const clean = value => String(value || '').replace(/\s+/g, ' ').trim();
  const norm = value => clean(value).toLowerCase();
  const wantedText = norm(locator.Text);
  const visible = el => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && cs.display !== 'none' &&
           cs.visibility !== 'hidden' && cs.pointerEvents !== 'none';
  };

  let pool = [];
  if (locator.Id) {
    const el = document.getElementById(locator.Id);
    if (el) pool = [el];
  } else if (locator.TestId) {
    const escaped = CSS.escape(locator.TestId);
    pool = [...document.querySelectorAll(`[data-testid="${escaped}"],[data-test-id="${escaped}"],[data-test="${escaped}"]`)];
  } else {
    pool = [...document.querySelectorAll('a,button,summary,[role],input,label,[aria-label]')];
  }

  const candidates = [];
  for (const el of pool.slice(0, 5000)) {
    if (!(el instanceof Element) || !visible(el)) continue;
    const r = el.getBoundingClientRect();
    const id = el.id || '';
    const testId = el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('data-test') || '';
    const role = el.getAttribute('role') || '';
    const aria = el.getAttribute('aria-label') || '';
    const name = el.getAttribute('name') || '';
    const href = el.href || el.getAttribute('href') || '';
    const text = clean(el.innerText || el.textContent || el.value || '').slice(0, 220);
    let score = 0;

    if (locator.Tag && el.tagName.toUpperCase() === locator.Tag.toUpperCase()) score += 7;
    if (locator.Id && id === locator.Id) score += 120;
    if (locator.TestId && testId === locator.TestId) score += 105;
    if (locator.Role && role === locator.Role) score += 30;
    if (locator.AriaLabel && aria === locator.AriaLabel) score += 55;
    if (locator.Name && name === locator.Name) score += 22;
    if (locator.Href && href === locator.Href) score += 45;

    const currentText = norm(text);
    if (wantedText && currentText === wantedText) score += 52;
    else if (wantedText && currentText &&
             (currentText.includes(wantedText) || wantedText.includes(currentText))) score += 20;

    // Historical geometry is weak evidence only.
    const dx = Math.abs((r.left + r.width * 0.5 + scrollX) - locator.DocumentX);
    const dy = Math.abs((r.top + r.height * 0.5 + scrollY) - locator.DocumentY);
    score += Math.max(0, 6 - (dx + dy) / 500);

    if (el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true') score -= 80;
    if (score > 0) candidates.push({ score, disabled: el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true' });
  }

  candidates.sort((a,b) => b.score - a.score);
  const best = candidates[0] || null;
  const second = candidates[1] || null;
  if (!best) return { resolved:false, score:0, gap:0, detail:'missing' };
  const gap = best.score - (second?.score || 0);
  const resolved = best.score >= 48 && gap >= 8 && !best.disabled;
  return {
    resolved,
    score: best.score,
    gap,
    detail: resolved ? 'resolved' : best.disabled ? 'disabled' : 'ambiguous'
  };
})()
""";

        try
        {
            using JsonDocument response = await client.EvaluateAsync(script, false);
            JsonElement value = response.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value");
            bool resolved = value.TryGetProperty("resolved", out JsonElement r) &&
                            r.ValueKind == JsonValueKind.True;
            double score = value.TryGetProperty("score", out JsonElement s) ? s.GetDouble() : 0;
            double gap = value.TryGetProperty("gap", out JsonElement g) ? g.GetDouble() : 0;
            string detail = value.TryGetProperty("detail", out JsonElement d)
                ? d.GetString() ?? string.Empty
                : string.Empty;
            return new ShareXModRecipeLocatorProbe(resolved, score, gap, detail);
        }
        catch
        {
            return new ShareXModRecipeLocatorProbe(false, 0, 0, "resolver-error");
        }
    }

    private static ShareXModRecipeTemplateRouterPlan Reject(string reason) =>
        new(
            false,
            reason,
            string.Empty,
            Array.Empty<ShareXModCaptureRecipeStep>(),
            Array.Empty<ShareXModRecipeTemplateFamily>(),
            0);
}
