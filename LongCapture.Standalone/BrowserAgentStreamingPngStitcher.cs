using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace LongCapture.Standalone;

internal static class BrowserAgentStreamingPngStitcher
{
    private const int PngChunkSize = 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static BrowserAgentStitchResult Stitch(
        string sessionDirectory,
        IReadOnlyList<BrowserAgentFrameRecord> frames,
        string outputPath)
    {
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("Browser Agent has no frames to stitch.");
        }

        BrowserAgentFrameRecord first = frames[0];
        ValidateFrameMetrics(first);
        if (Math.Abs(first.ScrollYCss) > 2.0)
        {
            throw new InvalidOperationException($"Browser Agent first frame did not start at the document top (scrollY={first.ScrollYCss:F2}).");
        }

        double scaleX = first.PixelWidth / first.ViewportWidthCss;
        double scaleY = first.PixelHeight / first.ViewportHeightCss;
        int width = first.PixelWidth;

        int[] starts = new int[frames.Count];
        int capturedBottom = 0;
        double maxScrollHeightCss = 0;
        double previousScrollY = double.NegativeInfinity;

        for (int i = 0; i < frames.Count; i++)
        {
            BrowserAgentFrameRecord frame = frames[i];
            ValidateFrameMetrics(frame);

            if (frame.PixelWidth != width)
            {
                throw new InvalidOperationException($"Browser Agent viewport pixel width changed at frame {frame.Sequence}: {frame.PixelWidth} != {width}.");
            }

            double frameScaleX = frame.PixelWidth / frame.ViewportWidthCss;
            double frameScaleY = frame.PixelHeight / frame.ViewportHeightCss;
            if (Math.Abs(frameScaleX - scaleX) > 0.02 || Math.Abs(frameScaleY - scaleY) > 0.02)
            {
                throw new InvalidOperationException(
                    $"Browser Agent viewport scale changed at frame {frame.Sequence}: {frameScaleX:F4}x{frameScaleY:F4} vs {scaleX:F4}x{scaleY:F4}.");
            }

            if (frame.ScrollYCss + 0.5 < previousScrollY)
            {
                throw new InvalidOperationException(
                    $"Browser Agent scroll geometry moved backwards at frame {frame.Sequence}: {frame.ScrollYCss:F2} < {previousScrollY:F2}.");
            }

            previousScrollY = frame.ScrollYCss;
            starts[i] = Math.Max(0, (int)Math.Round(frame.ScrollYCss * scaleY));
            capturedBottom = Math.Max(capturedBottom, starts[i] + frame.PixelHeight);
            maxScrollHeightCss = Math.Max(maxScrollHeightCss, frame.ScrollHeightCss);
        }

        bool completePage = frames[^1].AtBottom;
        int documentHeight = Math.Max(1, (int)Math.Round(maxScrollHeightCss * scaleY));
        int finalHeight = completePage ? Math.Min(documentHeight, capturedBottom) : capturedBottom;
        if (finalHeight <= 0)
        {
            throw new InvalidOperationException("Browser Agent computed an empty stitched image.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? sessionDirectory);
        string compressedPath = outputPath + ".idat.tmp";
        if (File.Exists(compressedPath)) File.Delete(compressedPath);

        int outputCursor = 0;
        try
        {
            using (var compressedFile = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zlib = new ZLibStream(compressedFile, CompressionLevel.Optimal, leaveOpen: false))
            {
                for (int i = 0; i < frames.Count && outputCursor < finalHeight; i++)
                {
                    BrowserAgentFrameRecord frame = frames[i];
                    int frameStart = starts[i];
                    int frameEnd = frameStart + frame.PixelHeight;

                    if (frameStart > outputCursor + 2)
                    {
                        throw new InvalidOperationException(
                            $"Browser Agent geometry contains a {frameStart - outputCursor}px gap before frame {frame.Sequence}.");
                    }

                    if (frameEnd <= outputCursor)
                    {
                        continue;
                    }

                    int nextDistinctStart = finalHeight;
                    for (int j = i + 1; j < frames.Count; j++)
                    {
                        if (starts[j] > outputCursor + 1)
                        {
                            nextDistinctStart = starts[j];
                            break;
                        }
                    }

                    int segmentEnd = Math.Min(finalHeight, Math.Min(frameEnd, nextDistinctStart));
                    if (segmentEnd <= outputCursor)
                    {
                        continue;
                    }

                    int localStartY = Math.Max(0, outputCursor - frameStart);
                    int rowCount = segmentEnd - outputCursor;
                    string framePath = Path.Combine(sessionDirectory, frame.FileName);
                    WriteRows(zlib, framePath, width, frame.PixelHeight, localStartY, rowCount);
                    outputCursor += rowCount;
                }
            }

            if (outputCursor != finalHeight)
            {
                throw new InvalidOperationException(
                    $"Browser Agent stitched {outputCursor}px but geometry requires {finalHeight}px. Refusing to invent missing pixels.");
            }

            WritePngContainer(outputPath, compressedPath, width, finalHeight);
        }
        finally
        {
            try
            {
                if (File.Exists(compressedPath)) File.Delete(compressedPath);
            }
            catch
            {
                // Diagnostics should not fail because a temporary compressed stream could not be removed.
            }
        }

        return new BrowserAgentStitchResult
        {
            OutputPath = outputPath,
            Width = width,
            Height = finalHeight,
            ScaleX = scaleX,
            ScaleY = scaleY,
            FrameCount = frames.Count
        };
    }

    private static void ValidateFrameMetrics(BrowserAgentFrameRecord frame)
    {
        if (string.IsNullOrWhiteSpace(frame.FileName) ||
            frame.ViewportWidthCss <= 0 || frame.ViewportHeightCss <= 0 ||
            frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            frame.ScrollHeightCss <= 0)
        {
            throw new InvalidOperationException($"Browser Agent frame {frame.Sequence} has invalid geometry metadata.");
        }
    }

    private static void WriteRows(
        Stream destination,
        string framePath,
        int expectedWidth,
        int expectedHeight,
        int localStartY,
        int rowCount)
    {
        using var source = new Bitmap(framePath);
        if (source.Width != expectedWidth || source.Height != expectedHeight)
        {
            throw new InvalidOperationException(
                $"Browser Agent PNG dimensions changed for {Path.GetFileName(framePath)}: {source.Width}x{source.Height}, expected {expectedWidth}x{expectedHeight}.");
        }

        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            byte[] bgra = new byte[expectedWidth * 4];
            byte[] pngRow = new byte[1 + expectedWidth * 4];
            pngRow[0] = 0; // PNG filter: None. Deterministic and cheap for the PoC.

            for (int y = localStartY; y < localStartY + rowCount; y++)
            {
                if (y < 0 || y >= expectedHeight)
                {
                    throw new InvalidOperationException($"Browser Agent row {y} is outside frame height {expectedHeight}.");
                }

                IntPtr rowPointer = data.Scan0 + y * data.Stride;
                Marshal.Copy(rowPointer, bgra, 0, bgra.Length);

                int target = 1;
                for (int x = 0; x < bgra.Length; x += 4)
                {
                    pngRow[target++] = bgra[x + 2]; // R
                    pngRow[target++] = bgra[x + 1]; // G
                    pngRow[target++] = bgra[x];     // B
                    pngRow[target++] = bgra[x + 3]; // A
                }

                destination.Write(pngRow, 0, pngRow.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void WritePngContainer(string outputPath, string compressedPath, int width, int height)
    {
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        output.Write(PngSignature, 0, PngSignature.Length);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR", ihdr);

        using var compressed = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[PngChunkSize];
        int read;
        while ((read = compressed.Read(buffer, 0, buffer.Length)) > 0)
        {
            WriteChunk(output, "IDAT", buffer.AsSpan(0, read));
        }

        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        output.Flush(true);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes, 0, typeBytes.Length);
        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
