#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShareX.ScreenCaptureLib;

internal sealed record ShareXModRouterProbeObservation(
    string Label,
    bool Strong,
    bool Usable,
    bool RequiredForSelection,
    int Weight,
    double Score,
    double Gap,
    string Detail);

internal sealed record ShareXModRouterEvidenceScore(
    bool Valid,
    double Score,
    string[] Evidence);

internal static class ShareXModTemplateRouterEvidenceScorer
{
    public static ShareXModRouterEvidenceScore Score(
        IReadOnlyCollection<ShareXModRouterProbeObservation> observations)
    {
        if (observations.Count == 0)
            return new ShareXModRouterEvidenceScore(false, 0, new[] { "no-semantic-evidence" });

        double sum = 0;
        int totalWeight = 0;
        List<string> evidence = new();

        foreach (ShareXModRouterProbeObservation observation in observations)
        {
            int weight = Math.Clamp(observation.Weight, 1, 20);
            double contribution = observation.Strong
                ? 1.0
                : observation.Usable
                    ? 0.72
                    : 0;

            evidence.Add(
                $"{observation.Label}:{observation.Detail}:score={observation.Score:0.0}:gap={observation.Gap:0.0}:weight={weight}");

            if (!observation.Usable && observation.RequiredForSelection)
            {
                evidence.Add(observation.Label + ":required-unresolved");
                return new ShareXModRouterEvidenceScore(false, 0, evidence.ToArray());
            }

            sum += contribution * weight;
            totalWeight += weight;
        }

        return new ShareXModRouterEvidenceScore(
            true,
            totalWeight == 0 ? 0 : sum / totalWeight,
            evidence.ToArray());
    }
}
