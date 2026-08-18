namespace LongCapture.Standalone;

internal sealed class BrowserAgentIntegratedController : IDisposable
{
    public const int SafetyFrameLimit = 1000;

    private readonly BrowserAgentBridgeServer bridge = new();
    private CancellationTokenSource? captureCancellation;
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
            captureCancellation.Dispose();
            captureCancellation = null;
            RaiseStateChanged();
        }
    }

    public void Stop()
    {
        try
        {
            captureCancellation?.Cancel();
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
        RaiseStateChanged();
    }

    private void OnAgentAttached(string summary)
    {
        AttachedSummary = string.IsNullOrWhiteSpace(summary) ? "Chromium tab attached" : summary;
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
