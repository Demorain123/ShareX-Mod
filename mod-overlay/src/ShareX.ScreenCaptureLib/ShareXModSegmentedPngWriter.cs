#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace ShareX.ScreenCaptureLib;

internal static class ShareXModSegmentedPngWriter
{
    public static void Write(string outputPath, IReadOnlyList<string> partPaths, int width, long totalHeight)
    {
        if (partPaths.Count == 0) throw new ArgumentException("No segmented parts supplied.", nameof(partPaths));
        if (width <= 0 || totalHeight <= 0 || totalHeight > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(totalHeight));

        string tempPath = outputPath + ".tmp";
        TryDelete(tempPath);

        using FileStream output = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), checked((uint)totalHeight));
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(output, "IHDR", ihdr);

        using PngChunkStream idat = new(output, 1024 * 1024);
        using (ZLibStream zlib = new(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (string path in partPaths)
            {
                using Bitmap bitmap = new(path);
                if (bitmap.Width != width) throw new InvalidDataException($"Segment width mismatch: {path}");
                WriteBitmapRows(zlib, bitmap);
            }
        }

        WriteChunk(output, "IEND", Array.Empty<byte>());
        output.Flush(true);
        output.Close();
        File.Move(tempPath, outputPath, true);
    }

    private static void WriteBitmapRows(Stream output, Bitmap source)
    {
        PixelFormat format = source.PixelFormat;
        int bits = Image.GetPixelFormatSize(format);
        Bitmap? normalized = null;

        if (bits != 24 && bits != 32)
        {
            normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(normalized);
            g.DrawImageUnscaled(source, 0, 0);
            source = normalized;
            format = source.PixelFormat;
            bits = 32;
        }

        Rectangle rect = new(0, 0, source.Width, source.Height);
        BitmapData data = source.LockBits(rect, ImageLockMode.ReadOnly, format);
        try
        {
            int bytesPerPixel = bits / 8;
            int rowBytes = checked(source.Width * bytesPerPixel);
            byte[] sourceRow = new byte[rowBytes];
            byte[] pngRow = new byte[checked(source.Width * 4 + 1)];

            for (int y = 0; y < source.Height; y++)
            {
                IntPtr pointer = IntPtr.Add(data.Scan0, checked(y * data.Stride));
                Marshal.Copy(pointer, sourceRow, 0, rowBytes);
                pngRow[0] = 0;

                for (int x = 0; x < source.Width; x++)
                {
                    int src = x * bytesPerPixel;
                    int dst = 1 + x * 4;
                    pngRow[dst] = sourceRow[src + 2];
                    pngRow[dst + 1] = sourceRow[src + 1];
                    pngRow[dst + 2] = sourceRow[src];
                    pngRow[dst + 3] = bytesPerPixel == 4 ? sourceRow[src + 3] : (byte)255;
                }

                output.Write(pngRow, 0, pngRow.Length);
            }
        }
        finally
        {
            source.UnlockBits(data);
            normalized?.Dispose();
        }
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        output.Write(typeBytes);
        if (data.Length > 0) output.Write(data);

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data) ^ 0xFFFFFFFFu;
        byte[] crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1u));
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }
        return crc;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class PngChunkStream : Stream
    {
        private readonly Stream output;
        private readonly byte[] buffer;
        private int length;

        public PngChunkStream(Stream output, int chunkSize)
        {
            this.output = output;
            buffer = new byte[Math.Max(64 * 1024, chunkSize)];
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] source, int offset, int count)
        {
            while (count > 0)
            {
                int copy = Math.Min(count, buffer.Length - length);
                Buffer.BlockCopy(source, offset, buffer, length, copy);
                length += copy;
                offset += copy;
                count -= copy;
                if (length == buffer.Length) FlushChunk();
            }
        }

        public override void Flush() { FlushChunk(); output.Flush(); }
        private void FlushChunk()
        {
            if (length == 0) return;
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(buffer, 0, chunk, 0, length);
            WriteChunk(output, "IDAT", chunk);
            length = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) FlushChunk();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
