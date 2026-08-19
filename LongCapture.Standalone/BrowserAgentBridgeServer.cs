using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace LongCapture.Standalone;

internal sealed class BrowserAgentBridgeServer : IDisposable
{
    internal const string PipeName = "LongCapture.BrowserAgent.v01";

    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim writerLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<long, byte> cancelledRequests = new();
    private readonly object pipeSync = new();
    private readonly Task listenerTask;
    private NamedPipeServerStream? activePipe;
    private long nextRequestId;
    private bool disposed;

    public event Action<bool>? ConnectionChanged;
    public event Action<string>? AgentAttached;

    public bool IsConnected
    {
        get
        {
            lock (pipeSync)
            {
                return activePipe?.IsConnected == true;
            }
        }
    }

    public BrowserAgentBridgeServer()
    {
        listenerTask = Task.Run(() => AcceptLoopAsync(cancellation.Token));
    }

    public async Task<JsonElement> SendRequestAsync(
        string type,
        object? payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Browser Agent request type is required.", nameof(type));
        }

        NamedPipeServerStream pipe;
        lock (pipeSync)
        {
            pipe = activePipe is { IsConnected: true }
                ? activePipe
                : throw new InvalidOperationException("Browser Agent extension is not connected.");
        }

        long id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Browser Agent request id collision.");
        }

        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            type,
            payload
        });

        try
        {
            await writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await BrowserAgentFrameCodec.WriteAsync(
                    pipe,
                    request,
                    BrowserAgentFrameCodec.MaxDesktopToExtensionBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writerLock.Release();
            }

            Task timeoutTask = Task.Delay(timeout, cancellationToken);
            Task completed = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);
            if (completed != completion.Task)
            {
                pending.TryRemove(id, out _);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"Browser Agent request '{type}' timed out after {timeout.TotalSeconds:F1}s.");
            }

            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pending.TryRemove(id, out _);
            cancelledRequests.TryAdd(id, 0);
            throw;
        }
        catch
        {
            pending.TryRemove(id, out _);
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();

        lock (pipeSync)
        {
            try
            {
                activePipe?.Dispose();
            }
            catch
            {
                // Best-effort shutdown.
            }
            activePipe = null;
        }

        FailPending(new ObjectDisposedException(nameof(BrowserAgentBridgeServer)));

        try
        {
            listenerTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (IOException)
        {
            // Expected if the pipe is disposed while waiting/reading.
        }

        writerLock.Dispose();
        cancellation.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            lock (pipeSync)
            {
                activePipe = pipe;
            }
            LongCaptureLog.Info("Browser Agent native host connected to desktop bridge");
            SafeRaiseConnectionChanged(true);

            try
            {
                await ReceiveLoopAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                // Chromium/native host disconnected.
            }
            catch (IOException ex)
            {
                LongCaptureLog.Warn($"Browser Agent pipe disconnected type={ex.GetType().Name} message={LongCaptureLog.OneLine(ex.Message)}");
            }
            catch (Exception ex)
            {
                LongCaptureLog.Error("Browser Agent receive loop failed", ex);
            }
            finally
            {
                lock (pipeSync)
                {
                    if (ReferenceEquals(activePipe, pipe))
                    {
                        activePipe = null;
                    }
                }
                FailPending(new IOException("Browser Agent extension disconnected."));
                cancelledRequests.Clear();
                SafeRaiseConnectionChanged(false);
                LongCaptureLog.Info("Browser Agent desktop bridge returned to listening state");
            }
        }
    }

    private async Task ReceiveLoopAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? payload = await BrowserAgentFrameCodec.ReadAsync(
                pipe,
                BrowserAgentFrameCodec.MaxExtensionToDesktopBytes,
                cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("type", out JsonElement typeElement) &&
                string.Equals(typeElement.GetString(), "agent.attached", StringComparison.Ordinal))
            {
                string summary = root.TryGetProperty("result", out JsonElement attached)
                    ? BuildAttachedSummary(attached)
                    : "Chromium tab attached";
                LongCaptureLog.Info($"Browser Agent attached {LongCaptureLog.OneLine(summary)}");
                SafeRaiseAgentAttached(summary);
                continue;
            }

            if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt64(out long id))
            {
                LongCaptureLog.Warn("Browser Agent ignored an uncorrelated message");
                continue;
            }

            if (!pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion))
            {
                if (cancelledRequests.TryRemove(id, out _))
                {
                    LongCaptureLog.Info($"Browser Agent discarded response for cancelled request id={id}");
                }
                else
                {
                    LongCaptureLog.Warn($"Browser Agent ignored late/unknown response id={id}");
                }
                continue;
            }

            bool ok = !root.TryGetProperty("ok", out JsonElement okElement) || okElement.GetBoolean();
            if (!ok)
            {
                string error = root.TryGetProperty("error", out JsonElement errorElement)
                    ? errorElement.GetString() ?? "Browser Agent command failed."
                    : "Browser Agent command failed.";
                completion.TrySetException(new InvalidOperationException(error));
                continue;
            }

            JsonElement result = root.TryGetProperty("result", out JsonElement resultElement)
                ? resultElement.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
            completion.TrySetResult(result);
        }
    }

    private static string BuildAttachedSummary(JsonElement attached)
    {
        string title = attached.TryGetProperty("title", out JsonElement titleElement)
            ? titleElement.GetString() ?? string.Empty
            : string.Empty;
        string url = attached.TryGetProperty("url", out JsonElement urlElement)
            ? urlElement.GetString() ?? string.Empty
            : string.Empty;
        return string.IsNullOrWhiteSpace(title) ? url : $"{title} | {url}";
    }

    private void FailPending(Exception exception)
    {
        foreach ((long id, TaskCompletionSource<JsonElement> completion) in pending.ToArray())
        {
            if (pending.TryRemove(id, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void SafeRaiseConnectionChanged(bool connected)
    {
        try
        {
            ConnectionChanged?.Invoke(connected);
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"Browser Agent ConnectionChanged subscriber failed: {LongCaptureLog.OneLine(ex.Message)}");
        }
    }

    private void SafeRaiseAgentAttached(string summary)
    {
        try
        {
            AgentAttached?.Invoke(summary);
        }
        catch (Exception ex)
        {
            LongCaptureLog.Warn($"Browser Agent AgentAttached subscriber failed: {LongCaptureLog.OneLine(ex.Message)}");
        }
    }
}
