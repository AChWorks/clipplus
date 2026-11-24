using System;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;
using ClipPlus.Core;

namespace ClipPlus;

internal static class ImagePreview
{
    internal static BitmapSource? Decode(ClipPayload payload)
    {
        if (payload.Formats.TryGetValue(ClipFormat.Png, out var png))
        {
            try { return DecodeBytes(png); }
            catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException) { }
        }
        foreach (var format in new[] { ClipFormat.DibV5, ClipFormat.Dib })
        {
            if (!payload.Formats.TryGetValue(format, out var dib)) continue;
            var bitmap = BitmapFile(dib);
            if (bitmap is not null)
            {
                try { return DecodeBytes(bitmap); }
                catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException) { }
            }
        }
        return null;
    }

    private static BitmapSource DecodeBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 152;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // A packed DIB starts at the bitmap-info header. A BMP file additionally
    // needs its 14-byte file header and an offset past masks/palette.
    // Unsupported layouts keep their original payload and show a text preview.
    private static byte[]? BitmapFile(byte[] dib)
    {
        if (dib.Length < 40 || dib.Length > Array.MaxLength - 14) return null;
        var header = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (header is not (40 or 52 or 56 or 108 or 124) || header > dib.Length) return null;
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        var colors = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        if (bits == 0 || bits > 32 || compression > 6) return null;
        if (colors == 0 && bits <= 8) colors = 1u << bits;
        var masks = header == 40 && compression == 3 ? 12 : header == 40 && compression == 6 ? 16 : 0;
        var offset = (long)header + masks + colors * 4L;
        if (offset >= dib.Length) return null;
        var bytes = new byte[dib.Length + 14];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), (int)offset + 14);
        dib.CopyTo(bytes, 14);
        return bytes;
    }
}
