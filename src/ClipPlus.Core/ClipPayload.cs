using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ClipPlus.Core;

public enum ClipColor { Default, Red, Orange, Yellow, Green, Cyan, Blue, Purple, Pink }
public enum ClipFormat { UnicodeText = 1, Html, Png, Dib, DibV5, NoCloudUpload }

public sealed class ClipPayload
{
    private readonly SortedDictionary<ClipFormat, byte[]> _formats = new();

    public ClipPayload(IEnumerable<KeyValuePair<ClipFormat, byte[]>> formats)
    {
        foreach (var (format, bytes) in formats)
        {
            if (!Enum.IsDefined(format) || bytes.Length == 0)
                continue;
            _formats[format] = Normalize(format, bytes);
        }
        if (!_formats.Keys.Any(format => format != ClipFormat.NoCloudUpload))
            throw new ArgumentException("Clipboard payload has no supported formats.", nameof(formats));
    }

    public IReadOnlyDictionary<ClipFormat, byte[]> Formats => _formats;
    public bool HasImage => _formats.ContainsKey(ClipFormat.Png) ||
        _formats.ContainsKey(ClipFormat.Dib) || _formats.ContainsKey(ClipFormat.DibV5);
    public string Text => _formats.TryGetValue(ClipFormat.UnicodeText, out var text)
        ? Encoding.Unicode.GetString(text.AsSpan(0, text.Length - 2)) : "";
    public bool HasHtml => _formats.ContainsKey(ClipFormat.Html);

    public string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[8];
        foreach (var (format, bytes) in _formats)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, (int)format);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], bytes.Length);
            hash.AppendData(header);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // GlobalAlloc may round up the requested memory size. Text formats end at
    // their terminator; preserve bytes preceding it, not allocator padding.
    private static byte[] Normalize(ClipFormat format, byte[] bytes)
    {
        if (format == ClipFormat.UnicodeText)
        {
            var length = bytes.Length & ~1;
            for (var i = 0; i < length; i += 2)
                if (bytes[i] == 0 && bytes[i + 1] == 0)
                {
                    length = i;
                    break;
                }
            var result = new byte[length + 2];
            bytes.AsSpan(0, length).CopyTo(result);
            return result;
        }
        if (format == ClipFormat.Html)
        {
            var end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            var result = new byte[end + 1];
            bytes.AsSpan(0, end).CopyTo(result);
            return result;
        }
        if (format == ClipFormat.NoCloudUpload && bytes.Length >= 4)
            return bytes.AsSpan(0, 4).ToArray();
        return bytes.ToArray();
    }

    public static ClipPayload FromText(string text) =>
        new(new Dictionary<ClipFormat, byte[]> { [ClipFormat.UnicodeText] = Encoding.Unicode.GetBytes(text + "\0") });
}

public sealed record ClipItem(long Id, string Title, ClipColor Color, bool Pinned,
    string Text, bool HasImage, bool HasHtml)
{
    public string DisplayTitle => string.IsNullOrEmpty(Title) ? "No Title" : Title;
    public string Preview => !string.IsNullOrEmpty(Text) ? Text :
        HasImage ? "Image" : HasHtml ? "HTML content" : "Empty text";
}
