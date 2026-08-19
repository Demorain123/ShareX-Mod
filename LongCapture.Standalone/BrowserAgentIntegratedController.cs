using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentIntegratedController : IDisposable
{
    public const int SafetyFrameLimit = 1000;

    private readonly BrowserAgentBridgeServer bridge = new();
    private CancellationTokenSource? captureCancellation;
    private int remoteStopDispatching;
    private bool disposed;

    public event Action? StateChanged;

    public bool IsConnected => bridge.IsConnected;
    public bool IsCapturing => captureCancellation is not null;
    public string AttachedSummary { get; private set; } = "No Chromium tab attached";
    public string? LastSessionDirectory { get; private set; }

    public BrowserAgentIntegratedController()
    {
        bridge.ConnectionChanged += OnConnectionChanged;
        bridge.AgentAttached += OnAgentAttached;
    }

    public Task<BrowserAgentStitchResult> CaptureAsync(Action<string>? status) =>
        CaptureAsync(BrowserAgentCaptureOptions.Default, status);

    public async Task<BrowserAgentStitchResult> CaptureAsync(
        BrowserAgentCaptureOptions options,
        Action<string>? status)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!bridge.IsConnected)
        {
            throw new InvalidOperationException(
                "Browser Agent is not attached. Activate the target Chromium/Helium tab and click the LongCapture extension icon (or press Ctrl+Shift+L) first.");
        }
        if (captureCancellation is not null)
        {
            throw new InvalidOperationException("Browser Assisted Capture is already running.");
        }

        options = (options ?? BrowserAgentCaptureOptions.Default).Normalize();
        captureCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref remoteStopDispatching, 0);
        LongCaptureLog.Info("[BA_TIMELINE] controller capture-start");
        RaiseStateChanged();
        var session = new BrowserAgentCaptureSession(bridge);
        try
        {
            BrowserAgentStitchResult result = await session.CaptureAsync(
                AppContext.BaseDirectory,
                SafetyFrameLimit,
                status,
                captureCancellation.Token,
                options).ConfigureAwait(false);
            LastSessionDirectory = session.LastSessionDirectory;
            return result;
        }
        catch
        {
            LastSessionDirectory = session.LastSessionDirectory;
            throw;
        }
        finally
        {
            LongCaptureLog.Info("[BA_TIMELINE] controller capture-finished");
            captureCancellation.Dispose();
            captureCancellation = null;
            RaiseStateChanged();
        }
    }

    public void Stop()
    {
        CancellationTokenSource? local = captureCancellation;
        if (local is null) return;

        LongCaptureLog.Info("[BA_TIMELINE] user-stop requested; forwarding explicit cancel to extension and cancelling desktop wait");

        // v0.1.4: cancelling the desktop Task alone is not sufficient. A long-running
        // chrome.scripting.executeScript() Promise keeps running in the extension even
        // after the desktop abandons its request, so send an out-of-band cancel command
        // over the same native-messaging Port as well.
        if (Interlocked.Exchange(ref remoteStopDispatching, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = await bridge.SendRequestAsync(
                        "cancel",
                        new { reason = "desktop-f8-stop", utc = DateTime.UtcNow.ToString("O") },
                        TimeSpan.FromSeconds(4),
                        CancellationToken.None).ConfigureAwait(false);
                    LongCaptureLog.Info("[BA_TIMELINE] extension cancel acknowledged");
                }
                catch (Exception ex)
                {
                    LongCaptureLog.Warn($"[BA_TIMELINE] extension cancel dispatch failed: {LongCaptureLog.OneLine(ex.Message)}");
                }
                finally
                {
                    Interlocked.Exchange(ref remoteStopDispatching, 0);
                }
            });
        }

        try
        {
            local.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrently finishing capture already owns cleanup.
        }
    }

    public string ExportLastDiagnostics()
    {
        if (string.IsNullOrWhiteSpace(LastSessionDirectory) || !Directory.Exists(LastSessionDirectory))
        {
            throw new DirectoryNotFoundException("No Browser Assisted session is available to export yet.");
        }
        return BrowserAgentDiagnosticsExporter.Export(LastSessionDirectory);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        bridge.ConnectionChanged -= OnConnectionChanged;
        bridge.AgentAttached -= OnAgentAttached;
        bridge.Dispose();
        captureCancellation?.Dispose();
        captureCancellation = null;
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!connected)
        {
            AttachedSummary = "No Chromium tab attached";
        }
        LongCaptureLog.Info($"[BA_TIMELINE] connection changed connected={connected}");
        RaiseStateChanged();
    }

    private void OnAgentAttached(string summary)
    {
        AttachedSummary = string.IsNullOrWhiteSpace(summary) ? "Chromium tab attached" : summary;
        LongCaptureLog.Info($"[BA_TIMELINE] tab attached target={LongCaptureLog.OneLine(AttachedSummary)}");
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"Browser Agent integrated state subscriber failed: {LongCaptureLog.OneLine(ex.Message)}");
        }
    }
}
