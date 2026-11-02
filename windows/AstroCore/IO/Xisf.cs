using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace AstroCore;

public enum XisfErrorKind { NotXisf, Truncated, Unsupported }

public sealed class XisfException : Exception
{
    public XisfErrorKind Kind { get; }
    public XisfException(XisfErrorKind kind, string? detail = null) : base(Describe(kind, detail)) { Kind = kind; }
    static string Describe(XisfErrorKind k, string? d) => k switch
    {
        XisfErrorKind.NotXisf => "Not an XISF file.",
        XisfErrorKind.Truncated => "XISF file is truncated.",
        _ => "Unsupported XISF: " + d,
    };
}

/// <summary>Result of loading the first image of a monolithic XISF file.</summary>
public sealed class XisfImage
{
    /// <summary>One plane per channel (1 for grayscale, 3 for RGB), rows top-down, samples in [0,1].</summary>
    public required IReadOnlyList<PlanarImage> Planes { get; init; }
    /// <summary>The image's FITSKeyword elements as a FITS header (values as written, quotes included).</summary>
    public required FitsHeader Header { get; init; }
    /// <summary>XISF properties with a text or scalar value (Instrument:Filter:Name, …).</summary>
    public required IReadOnlyDictionary<string, string> Properties { get; init; }
    /// <summary>Float32, UInt16, …</summary>
    public required string SampleFormat { get; init; }
    /// <summary>zlib, lz4, lz4hc, zstd (with +sh when byte-shuffled) or null.</summary>
    public string? Compression { get; init; }
    public int Width => Planes[0].Width;
    public int Height => Planes[0].Height;

    /// <summary>Filter name: the FITS keyword, else the XISF filter property.</summary>
    public string? FilterName => Header.String("FILTER") ?? Header.String("FILT-1") ?? (Properties.TryGetValue("Instrument:Filter:Name", out var f) ? f : null);
}

/// <summary>
/// Reader for monolithic XISF 1.0 files (PixInsight's native format): the first Image element,
/// attached or inline data, planar or interleaved storage, UInt8/16/32/64 and Float32/64 samples,
/// uncompressed, zlib or LZ4 compression (optionally byte-shuffled). Zstandard is not supported.
/// </summary>
public static class XisfReader
{
    static readonly byte[] Signature = Encoding.ASCII.GetBytes("XISF0100");

    public static XisfImage Read(string path) => Read(File.ReadAllBytes(path));

    public static XisfImage Read(byte[] data)
    {
        if (data.Length < 16 || !data.AsSpan(0, 8).SequenceEqual(Signature)) throw new XisfException(XisfErrorKind.NotXisf);
        int headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        if (data.Length < 16 + headerLength) throw new XisfException(XisfErrorKind.Truncated);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(data, 16, headerLength).TrimEnd('\0', ' '));
        var image = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Image") ?? throw new XisfException(XisfErrorKind.Unsupported, "no Image element");
        string? Attr(string name) => image.Attribute(name)?.Value;

        // Geometry "W:H:C" (XISF allows more dimensions; only 2-D images with channels are read).
        var geometry = (Attr("geometry") ?? "").Split(':').Select(t => int.TryParse(t, out var v) ? v : -1).ToArray();
        if (geometry.Length != 3 || geometry.Any(v => v <= 0)) throw new XisfException(XisfErrorKind.Unsupported, $"geometry {Attr("geometry") ?? "missing"}");
        int w = geometry[0], h = geometry[1], channels = geometry[2];
        if (channels > 4) throw new XisfException(XisfErrorKind.Unsupported, $"{channels} channels");
        string sampleFormat = Attr("sampleFormat") ?? "Float32";
        int bytesPer = sampleFormat switch
        {
            "UInt8" => 1, "UInt16" => 2, "UInt32" or "Float32" => 4, "UInt64" or "Float64" => 8,
            _ => throw new XisfException(XisfErrorKind.Unsupported, $"sample format {sampleFormat}"),
        };
        bool planar = (Attr("pixelStorage") ?? "Planar") != "Normal";
        bool bigEndian = (Attr("byteOrder") ?? "little") == "big";
        long expected = (long)w * h * channels * bytesPer;

        // Data block: attachment or inline.
        var location = (Attr("location") ?? "").Split(':');
        byte[] raw;
        switch (location[0])
        {
            case "attachment":
                if (location.Length < 3 || !long.TryParse(location[1], out var pos) || !long.TryParse(location[2], out var size)) throw new XisfException(XisfErrorKind.Unsupported, $"location {Attr("location")}");
                if (pos < 0 || size < 0 || pos + size > data.Length) throw new XisfException(XisfErrorKind.Truncated);
                raw = data.AsSpan((int)pos, (int)size).ToArray();
                break;
            case "inline":
                if (location.Length < 2 || location[1] != "base64") throw new XisfException(XisfErrorKind.Unsupported, "inline encoding");
                raw = Convert.FromBase64String(string.Concat(image.Value.Where(c => !char.IsWhiteSpace(c))));
                break;
            default: throw new XisfException(XisfErrorKind.Unsupported, $"location {Attr("location") ?? "missing"}");
        }

        // Compression "codec[+sh]:uncompressedSize[:itemSize]".
        string? compression = Attr("compression");
        if (compression != null)
        {
            var parts = compression.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[1], out int size)) throw new XisfException(XisfErrorKind.Unsupported, $"compression {compression}");
            bool shuffled = parts[0].EndsWith("+sh");
            string codec = shuffled ? parts[0][..^3] : parts[0];
            int itemSize = shuffled ? (parts.Length > 2 && int.TryParse(parts[2], out var isz) ? isz : bytesPer) : 1;
            raw = codec switch
            {
                "zlib" => Inflate(raw, size),
                "lz4" or "lz4hc" => Lz4.DecodeBlock(raw, size),
                _ => throw new XisfException(XisfErrorKind.Unsupported, $"{codec} compression (save the file uncompressed or with zlib / LZ4)"),
            };
            if (shuffled && itemSize > 1) raw = Unshuffle(raw, itemSize);
        }
        if (raw.Length < expected) throw new XisfException(XisfErrorKind.Truncated);

        // Samples → planes in [0,1].
        var planes = new List<PlanarImage>();
        int count = w * h;
        for (int c = 0; c < channels; c++)
        {
            var plane = PlanarImage.Uninitialized(w, h);
            int start = planar ? c * count * bytesPer : c * bytesPer;
            int stride = planar ? 1 : channels;
            Decode(raw, start, count, stride, sampleFormat, bigEndian, plane.Data);
            planes.Add(plane);
        }
        // Floating-point data outside [0,1] (bounds="lo:hi") is rescaled; integers are already normalised.
        if (sampleFormat.StartsWith("Float") && Attr("bounds") is string b)
        {
            var lohi = b.Split(':').Select(t => double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            if (lohi.Length == 2 && lohi[1] > lohi[0] && !(lohi[0] == 0 && lohi[1] == 1))
            {
                float a = (float)(1 / (lohi[1] - lohi[0]));
                foreach (var p in planes) p.Affine(a, -(float)lohi[0] * a);
            }
        }

        var header = new FitsHeader();
        foreach (var k in image.Elements().Where(e => e.Name.LocalName == "FITSKeyword"))
        {
            var name = k.Attribute("name")?.Value;
            if (name != null) header.Append(new FitsHeader.Card(name, k.Attribute("value")?.Value, k.Attribute("comment")?.Value));
        }
        var properties = new Dictionary<string, string>();
        foreach (var p in image.Elements().Concat(xml.Root!.Elements()).Where(e => e.Name.LocalName == "Property"))
        {
            var id = p.Attribute("id")?.Value;
            if (id == null || properties.ContainsKey(id)) continue;
            if (p.Attribute("value") is { } v) properties[id] = v.Value;
            else if (p.Attribute("location") == null && p.Value.Length > 0) properties[id] = p.Value;
        }
        return new XisfImage { Planes = planes, Header = header, Properties = properties, SampleFormat = sampleFormat, Compression = compression?.Split(':')[0] };
    }

    static void Decode(byte[] raw, int start, int count, int stride, string format, bool bigEndian, float[] out_)
    {
        var s = raw.AsSpan();
        switch (format)
        {
            case "UInt8":
                for (int i = 0; i < count; i++) out_[i] = s[start + i * stride] / 255f;
                break;
            case "UInt16":
                for (int i = 0; i < count; i++) { var sp = s.Slice(start + i * stride * 2, 2); out_[i] = (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(sp) : BinaryPrimitives.ReadUInt16LittleEndian(sp)) / 65535f; }
                break;
            case "UInt32":
                for (int i = 0; i < count; i++) { var sp = s.Slice(start + i * stride * 4, 4); out_[i] = (float)((bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(sp) : BinaryPrimitives.ReadUInt32LittleEndian(sp)) / 4294967295.0); }
                break;
            case "UInt64":
                for (int i = 0; i < count; i++) { var sp = s.Slice(start + i * stride * 8, 8); out_[i] = (float)((bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(sp) : BinaryPrimitives.ReadUInt64LittleEndian(sp)) / 18446744073709551615.0); }
                break;
            case "Float32":
                for (int i = 0; i < count; i++) { var sp = s.Slice(start + i * stride * 4, 4); out_[i] = bigEndian ? BinaryPrimitives.ReadSingleBigEndian(sp) : BinaryPrimitives.ReadSingleLittleEndian(sp); }
                break;
            case "Float64":
                for (int i = 0; i < count; i++) { var sp = s.Slice(start + i * stride * 8, 8); out_[i] = (float)(bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(sp) : BinaryPrimitives.ReadDoubleLittleEndian(sp)); }
                break;
        }
    }

    /// <summary>zlib stream (RFC 1950) → bytes.</summary>
    static byte[] Inflate(byte[] src, int uncompressedSize)
    {
        var out_ = new byte[uncompressedSize];
        using var z = new ZLibStream(new MemoryStream(src, false), CompressionMode.Decompress);
        int got = 0;
        while (got < uncompressedSize)
        {
            int n = z.Read(out_, got, uncompressedSize - got);
            if (n <= 0) break;
            got += n;
        }
        if (got != uncompressedSize) throw new XisfException(XisfErrorKind.Unsupported, $"zlib stream does not inflate to {uncompressedSize} bytes");
        return out_;
    }

    /// <summary>Undoes XISF byte shuffling: byte j of every item was stored contiguously.</summary>
    static byte[] Unshuffle(byte[] src, int itemSize)
    {
        int n = src.Length / itemSize;
        var out_ = new byte[src.Length];
        for (int j = 0; j < itemSize; j++)
        {
            int col = j * n;
            for (int i = 0; i < n; i++) out_[i * itemSize + j] = src[col + i];
        }
        for (int k = n * itemSize; k < src.Length; k++) out_[k] = src[k];
        return out_;
    }
}

/// <summary>LZ4 block format (no frame), as used by XISF.</summary>
public static class Lz4
{
    public static byte[] DecodeBlock(byte[] src, int uncompressedSize)
    {
        var d = new byte[uncompressedSize];
        int n = src.Length, i = 0, o = 0;
        while (i < n)
        {
            int token = src[i++];
            int literals = token >> 4;
            if (literals == 15)
            {
                while (true) { if (i >= n) throw new XisfException(XisfErrorKind.Truncated); int b = src[i++]; literals += b; if (b != 255) break; }
            }
            if (i + literals > n || o + literals > uncompressedSize) throw new XisfException(XisfErrorKind.Truncated);
            Array.Copy(src, i, d, o, literals);
            i += literals; o += literals;
            if (i >= n) break;   // the last sequence carries literals only
            if (i + 2 > n) throw new XisfException(XisfErrorKind.Truncated);
            int offset = src[i] | src[i + 1] << 8; i += 2;
            if (offset <= 0 || offset > o) throw new XisfException(XisfErrorKind.Unsupported, "corrupt LZ4 block");
            int length = (token & 15) + 4;
            if ((token & 15) == 15)
            {
                while (true) { if (i >= n) throw new XisfException(XisfErrorKind.Truncated); int b = src[i++]; length += b; if (b != 255) break; }
            }
            if (o + length > uncompressedSize) throw new XisfException(XisfErrorKind.Truncated);
            int from = o - offset;
            for (int k = 0; k < length; k++) d[o++] = d[from++];   // byte-wise: matches may overlap
        }
        if (o != uncompressedSize) throw new XisfException(XisfErrorKind.Truncated);
        return d;
    }
}
