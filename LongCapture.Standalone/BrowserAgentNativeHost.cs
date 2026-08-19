using System.IO.Pipes;

namespace LongCapture.Standalone;

internal static class BrowserAgentNativeHost
{
    internal const string HostName = "com.longcapture.browser_agent";

    public static bool IsChromeNativeMessagingLaunch(string[] args)
    {
        return args.Length > 0 &&
            args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
    }

    public static int Run(string[] args)
    {
        string origin = args.Length > 0 ? args[0] : "unknown";
        LongCaptureLog.Info($"Browser Agent native host starting origin={LongCaptureLog.OneLine(origin)}");

        try
        {
            using Stream stdin = Console.OpenStandardInput();
            using Stream stdout = Console.OpenStandardOutput();
            using var pipe = new NamedPipeClientStream(
                ".",
                BrowserAgentBridgeServer.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            // The Browser Agent PoC window must already be open before the user
            // clicks the extension action. Keeping this timeout short makes a stale
            // extension click fail cleanly instead of leaving a hidden host process.
            pipe.Connect(5000);

            using var cancellation = new CancellationTokenSource();
            Task extensionToDesktop = PumpAsync(
                stdin,
                pipe,
                BrowserAgentFrameCodec.MaxExtensionToDesktopBytes,
                BrowserAgentFrameCodec.MaxExtensionToDesktopBytes,
                cancellation.Token);
            Task desktopToExtension = PumpAsync(
                pipe,
                stdout,
                BrowserAgentFrameCodec.MaxDesktopToExtensionBytes,
                BrowserAgentFrameCodec.MaxDesktopToExtensionBytes,
                cancellation.Token);

            Task.WhenAny(extensionToDesktop, desktopToExtension).GetAwaiter().GetResult();
            cancellation.Cancel();

            // Do not synchronously wait for the losing stdio read. Some Windows
            // standard-input handles do not observe cancellation until the handle is
            // closed. Returning disposes all three streams and lets the short-lived
            // native-host process terminate immediately with Chrome's port.
            LongCaptureLog.Info("Browser Agent native host peer closed; terminating proxy");
            return 0;
        }
        catch (TimeoutException)
        {
            LongCaptureLog.Warn("Browser Agent native host could not connect to the desktop PoC pipe within 5 seconds");
            return 41;
        }
        catch (Exception ex)
        {
            LongCaptureLog.Error("Browser Agent native host failed", ex);
            return 42;
        }
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        int sourceMaxBytes,
        int destinationMaxBytes,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? payload = await BrowserAgentFrameCodec.ReadAsync(source, sourceMaxBytes, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                break;
            }

            await BrowserAgentFrameCodec.WriteAsync(destination, payload, destinationMaxBytes, cancellationToken).ConfigureAwait(false);
        }
    }
}
