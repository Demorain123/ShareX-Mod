#nullable enable

using System;
using System.IO;
using System.Text.Json;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModCaptureIntegritySelfTests
{
    public static string RunOrThrow()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "sharex-mod-integrity-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllBytes(Path.Combine(root, "capture-full.png"), new byte[] { 1, 2, 3, 4, 5, 6 });
            File.WriteAllText(Path.Combine(root, "run.json"), "{\"ok\":true}");
            string originals = Path.Combine(root, "image-appendix");
            Directory.CreateDirectory(originals);
            File.WriteAllBytes(Path.Combine(originals, "native.jpg"), new byte[] { 9, 8, 7, 6, 5 });
            File.WriteAllText(Path.Combine(originals, "page-loop-image-collection.json"), "{\"entries\":[]}");

            string manifest = ShareXModCaptureIntegrityManifest.WriteDirectoryAsync(
                    root,
                    includeNativeAssets: false,
                    maxFiles: 100)
                .GetAwaiter()
                .GetResult();

            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest)))
            {
                JsonElement files = doc.RootElement.GetProperty("files");
                Assert(files.GetArrayLength() == 3,
                    $"expected 3 hashed core files (primary, run metadata, native audit), got {files.GetArrayLength()}");
                Assert(!File.ReadAllText(manifest).Contains("native.jpg", StringComparison.OrdinalIgnoreCase),
                    "native original bytes must not be re-hashed when includeNativeAssets=false");
            }

            ShareXModIntegrityVerificationResult first =
                ShareXModCaptureIntegrityManifest.VerifyAsync(root, manifest)
                    .GetAwaiter()
                    .GetResult();
            Assert(first.Valid, "fresh integrity manifest must verify");

            File.AppendAllText(Path.Combine(root, "run.json"), " ");
            ShareXModIntegrityVerificationResult tampered =
                ShareXModCaptureIntegrityManifest.VerifyAsync(root, manifest)
                    .GetAwaiter()
                    .GetResult();
            Assert(!tampered.Valid && tampered.MismatchedFiles > 0,
                "modifying a saved output file must fail integrity verification");

            return "ShareX-Mod post-capture integrity self-tests passed: 1";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("ShareX-Mod integrity self-test failed: " + message);
    }
}
