#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal enum ShareXModChromeConnectionProviderKind
{
    ConfiguredEndpoint,
    EnvironmentEndpoint,
    LocalDiscovery
}

internal sealed record ShareXModChromeConnectionResolution(
    bool Connected,
    ShareXModChromeConnectionProviderKind Provider,
    string Endpoint,
    string Detail,
    IReadOnlyList<ShareXModChromeTarget> Targets);

internal static class ShareXModChromeConnectionBroker
{
    public static async Task<ShareXModChromeConnectionResolution> ResolveAsync(
        ShareXModChromeCdpClient client,
        ShareXModV04Settings settings,
        CancellationToken cancellationToken = default)
    {
        List<(ShareXModChromeConnectionProviderKind provider, string endpoint)> candidates =
            BuildCandidates(settings);

        List<string> failures = new();

        foreach ((ShareXModChromeConnectionProviderKind provider, string endpoint) in candidates)
        {
            if (!IsEndpointAllowed(endpoint, settings.ChromeAllowNonLoopbackCdp))
            {
                failures.Add($"{provider}: refused non-loopback endpoint");
                continue;
            }

            try
            {
                using CancellationTokenSource perCandidate =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                perCandidate.CancelAfter(
                    Math.Clamp(settings.ChromeConnectionProbeTimeoutMs, 250, 5000));

                IReadOnlyList<ShareXModChromeTarget> targets =
                    await client.ListTargetsAsync(endpoint, perCandidate.Token);

                if (targets.Count > 0)
                {
                    return new ShareXModChromeConnectionResolution(
                        true,
                        provider,
                        endpoint,
                        $"{provider} · {targets.Count} page target(s)",
                        targets);
                }

                failures.Add($"{provider}: reachable, no page targets");
            }
            catch (OperationCanceledException)
            {
                failures.Add($"{provider}: timeout");
            }
            catch
            {
                failures.Add($"{provider}: unavailable");
            }
        }

        return new ShareXModChromeConnectionResolution(
            false,
            candidates.Count > 0 ? candidates[0].provider : ShareXModChromeConnectionProviderKind.ConfiguredEndpoint,
            candidates.Count > 0 ? candidates[0].endpoint : string.Empty,
            failures.Count == 0
                ? "No Chrome DevTools connection candidate was configured."
                : string.Join("; ", failures),
            Array.Empty<ShareXModChromeTarget>());
    }

    private static List<(ShareXModChromeConnectionProviderKind provider, string endpoint)> BuildCandidates(
        ShareXModV04Settings settings)
    {
        List<(ShareXModChromeConnectionProviderKind provider, string endpoint)> result = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Add(ShareXModChromeConnectionProviderKind provider, string? endpoint)
        {
            string normalized = Normalize(endpoint);
            if (normalized.Length == 0 || !seen.Add(normalized)) return;
            result.Add((provider, normalized));
        }

        Add(ShareXModChromeConnectionProviderKind.ConfiguredEndpoint, settings.ChromeCdpEndpoint);
        Add(
            ShareXModChromeConnectionProviderKind.EnvironmentEndpoint,
            Environment.GetEnvironmentVariable("SHAREX_MOD_CDP_ENDPOINT"));

        if (settings.ChromeAutoDiscoverLocalCdp)
        {
            int start = Math.Clamp(settings.ChromeAutoDiscoverPortStart, 1024, 65535);
            int count = Math.Clamp(settings.ChromeAutoDiscoverPortCount, 1, 12);

            for (int i = 0; i < count && start + i <= 65535; i++)
            {
                Add(
                    ShareXModChromeConnectionProviderKind.LocalDiscovery,
                    $"http://127.0.0.1:{start + i}");
            }
        }

        return result;
    }

    private static string Normalize(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        return endpoint.Trim().TrimEnd('/');
    }

    private static bool IsEndpointAllowed(string endpoint, bool allowNonLoopback)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (allowNonLoopback) return true;

        return uri.IsLoopback ||
               uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }
}
