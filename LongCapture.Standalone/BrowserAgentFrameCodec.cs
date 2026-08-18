using System.Buffers.Binary;

namespace LongCapture.Standalone;

internal static class BrowserAgentFrameCodec
{
    internal const int MaxExtensionToDesktopBytes = 64 * 1024 * 1024;
    internal const int MaxDesktopToExtensionBytes = 1024 * 1024;

    public static async Task<byte[]?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[4];
        int lengthRead = await ReadExactOrEofAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        if (lengthRead == 0)
        {
            return null;
        }

        if (lengthRead != 4)
        {
            throw new EndOfStreamException("Browser Agent frame ended inside the length prefix.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > maxBytes)
        {
            throw new InvalidDataException($"Browser Agent frame length {length} is outside 1..{maxBytes} bytes.");
        }

        byte[] payload = new byte[length];
        int payloadRead = await ReadExactOrEofAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead != length)
        {
            throw new EndOfStreamException($"Browser Agent frame expected {length} payload bytes but received {payloadRead}.");
        }

        return payload;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, int maxBytes, CancellationToken cancellationToken)
    {
        if (payload.Length <= 0 || payload.Length > maxBytes)
        {
            throw new InvalidDataException($"Browser Agent payload length {payload.Length} is outside 1..{maxBytes} bytes.");
        }

        byte[] lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
