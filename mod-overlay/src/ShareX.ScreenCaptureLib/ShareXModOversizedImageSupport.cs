#nullable enable

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

internal sealed class ShareXModOversizedSaveResult
{
    public string OutputPath { get; init; } = string.Empty;
    public bool IsSinglePng { get; init; }
    public int PartCount { get; init; }
}

internal static class ShareXModOversizedImageSupport
{
    private static readonly object Sync = new();
    private static WeakReference<Bitmap>? lastBitmap;
    private static ShareXModOversizedSaveResult? lastSaveResult;

    public static bool IsOversized(Bitmap? bitmap)
    {
        if (bitmap == null)
        {
            return false;
        }

        ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();
        return settings.OversizedCaptureEnabled &&
            (bitmap.Width > settings.OversizedDimensionThreshold || bitmap.Height > settings.OversizedDimensionThreshold);
    }

    public static Bitmap CreatePreview(Bitmap source)
    {
        ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();
        int maxWidth = Math.Max(256, settings.OversizedPreviewMaxWidth);
        int maxHeight = Math.Max(512, settings.OversizedPreviewMaxHeight);

        double scale = Math.Min(1.0, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        Bitmap preview = new(width, height, PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(preview))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source,
                new Rectangle(0, 0, width, height),
                new Rectangle(0, 0, source.Width, source.Height),
                GraphicsUnit.Pixel);
        }

        return preview;
    }

    public static Task<ShareXModOversizedSaveResult> SaveAsync(Bitmap source)
    {
        return Task.Run(() => Save(source));
    }

    public static bool TryOpenSavedOutput(Bitmap? bitmap)
    {
        if (bitmap == null)
        {
            return false;
        }

        ShareXModOversizedSaveResult? result = null;

        lock (Sync)
        {
            if (lastBitmap != null && lastBitmap.TryGetTarget(out Bitmap? target) && ReferenceEquals(target, bitmap))
            {
                result = lastSaveResult;
            }
        }

        if (result == null || string.IsNullOrWhiteSpace(result.OutputPath))
        {
            return false;
        }

        string path = result.OutputPath;
        string selection = File.Exists(path) ? path : Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

        try
        {
            if (File.Exists(selection))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{selection}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = selection,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static ShareXModOversizedSaveResult Save(Bitmap source)
    {
        ShareXModRobustScrollingSettings settings = ShareXModRobustScrollingSettings.Load();
        string outputDirectory = ResolveOutputDirectory(settings.OversizedOutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        string baseName = $"ShareX-Mod-{DateTime.Now:yyyyMMdd-HHmmss}-{source.Width}x{source.Height}";
        string pngPath = Path.Combine(outputDirectory, baseName + ".png");

        ShareXModOversizedSaveResult result;

        if (settings.OversizedPreferSinglePng)
        {
            try
            {
                SavePngWithoutGdiEncoder(source, pngPath);
                result = new ShareXModOversizedSaveResult
                {
                    OutputPath = pngPath,
                    IsSinglePng = true,
                    PartCount = 1
                };
                Register(source, result);
                WriteMetadata(source, result, outputDirectory, baseName);
                return result;
            }
            catch
            {
                TryDelete(pngPath);
            }
        }

        result = SaveParts(source, outputDirectory, baseName, settings.OversizedFallbackPartHeight);
        Register(source, result);
        WriteMetadata(source, result, outputDirectory, baseName);
        return result;
    }

    private static string ResolveOutputDirectory(string configuredPath)
    {
        string relative = string.IsNullOrWhiteSpace(configuredPath)
            ? "ShareX-Mod\\OversizedCaptures"
            : configuredPath;

        string primary = Path.IsPathRooted(relative)
            ? relative
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));

        try
        {
            Directory.CreateDirectory(primary);
            string probe = Path.Combine(primary, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return primary;
        }
        catch
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string fallback = Path.Combine(local, "ShareX-Mod", "OversizedCaptures");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static ShareXModOversizedSaveResult SaveParts(Bitmap source, string outputDirectory, string baseName, int configuredPartHeight)
    {
        int partHeight = Math.Clamp(configuredPartHeight, 4096, 60000);
        string partsDirectory = Path.Combine(outputDirectory, baseName + "-parts");
        Directory.CreateDirectory(partsDirectory);

        int partCount = (source.Height + partHeight - 1) / partHeight;
        string[] parts = new string[partCount];

        for (int i = 0; i < partCount; i++)
        {
            int y = i * partHeight;
            int height = Math.Min(partHeight, source.Height - y);
            Rectangle rect = new(0, y, source.Width, height);

            using Bitmap part = source.Clone(rect, PixelFormat.Format32bppArgb);
            string partPath = Path.Combine(partsDirectory, $"part_{i + 1:D4}.png");
            part.Save(partPath, ImageFormat.Png);
            parts[i] = Path.GetFileName(partPath);
        }

        string manifestPath = Path.Combine(partsDirectory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            format = "ShareX-Mod oversized capture parts",
            width = source.Width,
            height = source.Height,
            partHeight,
            partCount,
            parts
        }, new JsonSerializerOptions { WriteIndented = true }));

        return new ShareXModOversizedSaveResult
        {
            OutputPath = manifestPath,
            IsSinglePng = false,
            PartCount = partCount
        };
    }

    private static void SavePngWithoutGdiEncoder(Bitmap source, string outputPath)
    {
        string tempPath = outputPath + ".tmp";
        TryDelete(tempPath);

        using FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);

        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), checked((uint)source.Width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), checked((uint)source.Height));
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);

        Rectangle rect = new(0, 0, source.Width, source.Height);
        PixelFormat pixelFormat = source.PixelFormat;
        int bitsPerPixel = Image.GetPixelFormatSize(pixelFormat);

        if (bitsPerPixel != 32 && bitsPerPixel != 24)
        {
            throw new NotSupportedException($"Unsupported oversized PNG source pixel format: {pixelFormat} ({bitsPerPixel} bpp)");
        }

        BitmapData data = source.LockBits(rect, ImageLockMode.ReadOnly, pixelFormat);

        try
        {
            int sourceBytesPerPixel = bitsPerPixel / 8;
            int sourceRowBytes = checked(source.Width * sourceBytesPerPixel);
            byte[] sourceRow = new byte[sourceRowBytes];
            byte[] pngRow = new byte[checked(source.Width * 4 + 1)];

            using PngIdatChunkStream idatStream = new(output, 1024 * 1024);
            using (ZLibStream zlib = new(idatStream, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (int y = 0; y < source.Height; y++)
                {
                    IntPtr rowPointer = IntPtr.Add(data.Scan0, checked(y * data.Stride));
                    Marshal.Copy(rowPointer, sourceRow, 0, sourceRowBytes);
                    pngRow[0] = 0;

                    if (sourceBytesPerPixel == 4)
                    {
                        bool premultiplied = pixelFormat == PixelFormat.Format32bppPArgb;
                        bool opaqueRgb = pixelFormat == PixelFormat.Format32bppRgb;

                        for (int x = 0; x < source.Width; x++)
                        {
                            int sourceIndex = x * 4;
                            int targetIndex = 1 + x * 4;
                            byte b = sourceRow[sourceIndex];
                            byte g = sourceRow[sourceIndex + 1];
                            byte r = sourceRow[sourceIndex + 2];
                            byte a = opaqueRgb ? (byte)255 : sourceRow[sourceIndex + 3];

                            if (premultiplied && a > 0 && a < 255)
                            {
                                r = Unpremultiply(r, a);
                                g = Unpremultiply(g, a);
                                b = Unpremultiply(b, a);
                            }

                            pngRow[targetIndex] = r;
                            pngRow[targetIndex + 1] = g;
                            pngRow[targetIndex + 2] = b;
                            pngRow[targetIndex + 3] = a;
                        }
                    }
                    else
                    {
                        for (int x = 0; x < source.Width; x++)
                        {
                            int sourceIndex = x * 3;
                            int targetIndex = 1 + x * 4;
                            pngRow[targetIndex] = sourceRow[sourceIndex + 2];
                            pngRow[targetIndex + 1] = sourceRow[sourceIndex + 1];
                            pngRow[targetIndex + 2] = sourceRow[sourceIndex];
                            pngRow[targetIndex + 3] = 255;
                        }
                    }

                    zlib.Write(pngRow, 0, pngRow.Length);
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
        }

        WriteChunk(output, "IEND", Array.Empty<byte>());
        output.Flush(true);
        output.Close();

        File.Move(tempPath, outputPath, overwrite: true);
    }

    private static byte Unpremultiply(byte component, byte alpha)
    {
        int value = (component * 255 + alpha / 2) / alpha;
        return (byte)Math.Min(255, value);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length, 0, length.Length);
        output.Write(typeBytes, 0, typeBytes.Length);
        if (data.Length > 0)
        {
            output.Write(data, 0, data.Length);
        }

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, typeBytes, 0, typeBytes.Length);
        crc = UpdateCrc(crc, data, 0, data.Length) ^ 0xFFFFFFFFu;
        byte[] crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes, 0, crcBytes.Length);
    }

    private static uint UpdateCrc(uint crc, byte[] bytes, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            crc ^= bytes[offset + i];
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1u));
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }
        return crc;
    }

    private static void Register(Bitmap bitmap, ShareXModOversizedSaveResult result)
    {
        lock (Sync)
        {
            lastBitmap = new WeakReference<Bitmap>(bitmap);
            lastSaveResult = result;
        }
    }

    private static void WriteMetadata(Bitmap source, ShareXModOversizedSaveResult result, string directory, string baseName)
    {
        try
        {
            string metadataPath = Path.Combine(directory, baseName + ".json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
            {
                format = "ShareX-Mod oversized capture",
                created = DateTimeOffset.Now,
                width = source.Width,
                height = source.Height,
                singlePng = result.IsSinglePng,
                partCount = result.PartCount,
                outputPath = result.OutputPath
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class PngIdatChunkStream : Stream
    {
        private readonly Stream output;
        private readonly byte[] buffer;
        private int bufferLength;
        private bool disposed;

        public PngIdatChunkStream(Stream output, int chunkSize)
        {
            this.output = output;
            buffer = new byte[Math.Max(64 * 1024, chunkSize)];
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            FlushChunk();
            output.Flush();
        }

        public override void Write(byte[] source, int offset, int count)
        {
            while (count > 0)
            {
                int copy = Math.Min(count, buffer.Length - bufferLength);
                Buffer.BlockCopy(source, offset, buffer, bufferLength, copy);
                bufferLength += copy;
                offset += copy;
                count -= copy;
                if (bufferLength == buffer.Length)
                {
                    FlushChunk();
                }
            }
        }

        public override void Write(ReadOnlySpan<byte> source)
        {
            while (!source.IsEmpty)
            {
                int copy = Math.Min(source.Length, buffer.Length - bufferLength);
                source[..copy].CopyTo(buffer.AsSpan(bufferLength));
                bufferLength += copy;
                source = source[copy..];
                if (bufferLength == buffer.Length)
                {
                    FlushChunk();
                }
            }
        }

        private void FlushChunk()
        {
            if (bufferLength <= 0)
            {
                return;
            }
            byte[] chunk = new byte[bufferLength];
            Buffer.BlockCopy(buffer, 0, chunk, 0, bufferLength);
            WriteChunk(output, "IDAT", chunk);
            bufferLength = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    FlushChunk();
                }
                disposed = true;
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public partial class ScrollingCaptureWindow
{
    private async Task LoadShareXModImageAsync(Bitmap? bitmap)
    {
        if (bitmap == null || !ShareXModOversizedImageSupport.IsOversized(bitmap))
        {
            LoadImage(bitmap);
            return;
        }

        int originalWidth = bitmap.Width;
        int originalHeight = bitmap.Height;

        try
        {
            StatusText.Text = $"Oversized capture {originalWidth}x{originalHeight}: preparing preview...";
            using (Bitmap preview = await Task.Run(() => ShareXModOversizedImageSupport.CreatePreview(bitmap)))
            {
                LoadImage(preview);
            }

            ResultSizeText.Text = $"{originalWidth}x{originalHeight}";
            CopyButton.IsEnabled = false;
            StatusText.Text = $"Oversized capture {originalWidth}x{originalHeight}: saving lossless output...";

            ShareXModOversizedSaveResult result = await ShareXModOversizedImageSupport.SaveAsync(bitmap);
            ResultSizeText.Text = $"{originalWidth}x{originalHeight}";
            CopyButton.IsEnabled = false;
            UploadButton.IsEnabled = false;

            StatusText.Text = result.IsSinglePng
                ? $"Oversized capture saved: {result.OutputPath}"
                : $"Oversized capture saved as {result.PartCount} PNG parts: {Path.GetDirectoryName(result.OutputPath)}";
        }
        catch (Exception ex)
        {
            CopyButton.IsEnabled = false;
            UploadButton.IsEnabled = false;
            StatusText.Text = $"Oversized capture output failed: {ex.Message}";
        }
    }
}
