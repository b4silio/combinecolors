using System.Buffers.Binary;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AstroCore;

public enum FitsErrorKind { NotFits, Truncated, Unsupported, WriteFailed }

public sealed class FitsException : Exception
{
    public FitsErrorKind Kind { get; }
    public FitsException(FitsErrorKind kind, string? detail = null) : base(Describe(kind, detail)) { Kind = kind; }

    static string Describe(FitsErrorKind k, string? d) => k switch
    {
        FitsErrorKind.NotFits => "The file is not a FITS file.",
        FitsErrorKind.Truncated => "The FITS file is truncated.",
        FitsErrorKind.Unsupported => $"Unsupported FITS file: {d}",
        _ => $"Could not write FITS file: {d}",
    };
}

/// <summary>A parsed FITS header: ordered cards plus keyword lookup.</summary>
public sealed class FitsHeader
{
    public readonly record struct Card(string Key, string? Value, string? Comment);

    readonly List<Card> cards = new();
    readonly Dictionary<string, int> index = new();

    public IReadOnlyList<Card> Cards => cards;

    public void Append(Card c)
    {
        if (!index.ContainsKey(c.Key)) index[c.Key] = cards.Count;
        cards.Add(c);
    }

    public string? this[string key] => index.TryGetValue(key.ToUpperInvariant(), out var i) ? cards[i].Value : null;

    public string? String(string key)
    {
        var v = this[key];
        if (v == null) return null;
        var s = v.Trim(' ');
        if (s.StartsWith('\''))
        {
            s = s[1..];
            int q = s.LastIndexOf('\'');
            if (q >= 0) s = s[..q];
            s = s.Replace("''", "'");
        }
        return s.Trim(' ');
    }

    public int? Int(string key)
    {
        var v = this[key];
        if (v == null) return null;
        return long.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? (int)l : null;
    }

    public double? Double(string key)
    {
        var v = this[key];
        if (v == null) return null;
        return double.TryParse(v.Trim().Replace("D", "E"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public bool? Bool(string key)
    {
        var v = this[key]?.Trim();
        if (v == null) return null;
        return v == "T" ? true : (v == "F" ? false : null);
    }

    /// <summary>Parse one 80-character card.</summary>
    public static Card? ParseCard(string s)
    {
        var key = (s.Length >= 8 ? s[..8] : s).Trim(' ');
        if (key.Length == 0) return null;
        if (key == "COMMENT" || key == "HISTORY" || key == "END")
            return new Card(key, null, (s.Length > 8 ? s[8..] : "").Trim(' '));
        var afterKey = s.Length > 8 ? s[8..] : "";
        if (!(s.Length >= 10 && (afterKey.StartsWith("= ") || afterKey.StartsWith("="))))
            return new Card(key, null, afterKey.Trim(' '));
        var rest = s.Length > 9 ? s[9..] : "";
        string value = rest; string? comment = null;
        var trimmed = rest.Trim(' ');
        if (trimmed.StartsWith('\''))
        {
            int i = 1;
            bool closed = false;
            while (i < trimmed.Length)
            {
                if (trimmed[i] == '\'')
                {
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == '\'') { i += 2; continue; }
                    closed = true;
                    value = trimmed[..(i + 1)];
                    var after = trimmed[(i + 1)..].Trim(' ');
                    if (after.StartsWith('/')) comment = after[1..].Trim(' ');
                    break;
                }
                i++;
            }
            if (!closed) value = trimmed;
        }
        else
        {
            int slash = trimmed.IndexOf('/');
            if (slash >= 0)
            {
                value = trimmed[..slash].Trim(' ');
                comment = trimmed[(slash + 1)..].Trim(' ');
            }
            else value = trimmed;
        }
        return new Card(key, value, comment);
    }
}

/// <summary>Result of loading a FITS primary HDU.</summary>
public sealed class FitsImage
{
    public FitsHeader Header { get; }
    /// <summary>One plane per NAXIS3 slice (1 for mono, 3 for RGB).</summary>
    public IReadOnlyList<PlanarImage> Planes { get; }
    public int Bitpix { get; }
    public int Width => Planes[0].Width;
    public int Height => Planes[0].Height;

    public FitsImage(FitsHeader header, IReadOnlyList<PlanarImage> planes, int bitpix) { Header = header; Planes = planes; Bitpix = bitpix; }
}

public static class FitsReader
{
    public const int BlockSize = 2880;

    public static FitsImage Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Read(fs);
    }

    public static FitsHeader ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadHeader(fs);
    }

    /// <summary>Parses an in-memory FITS file (primary HDU only).</summary>
    public static FitsImage Read(byte[] data) => Read(new MemoryStream(data, false));

    static FitsHeader ReadHeader(Stream s)
    {
        var header = new FitsHeader();
        var block = new byte[BlockSize];
        bool first = true, done = false;
        while (!done)
        {
            if (!ReadExactly(s, block)) throw new FitsException(first ? FitsErrorKind.NotFits : FitsErrorKind.Truncated);
            if (first)
            {
                if (!block.AsSpan(0, 9).SequenceEqual("SIMPLE  ="u8)) throw new FitsException(FitsErrorKind.NotFits);
                first = false;
            }
            done = ParseBlock(block, header);
        }
        return header;
    }

    public static FitsImage Read(Stream s)
    {
        if (s.CanSeek && s.Length < BlockSize) throw new FitsException(FitsErrorKind.NotFits);
        var header = ReadHeader(s);
        int? bp = header.Int("BITPIX"), na = header.Int("NAXIS");
        if (bp is not int bitpix || na is not int naxis) throw new FitsException(FitsErrorKind.Unsupported, "missing BITPIX/NAXIS");
        if (naxis != 2 && naxis != 3) throw new FitsException(FitsErrorKind.Unsupported, $"NAXIS = {naxis}");
        int w = header.Int("NAXIS1") ?? 0, h = header.Int("NAXIS2") ?? 0;
        if (w <= 0 || h <= 0) throw new FitsException(FitsErrorKind.Unsupported, "bad dimensions");
        int nplanes = naxis == 3 ? (header.Int("NAXIS3") ?? 1) : 1;
        if (nplanes < 1 || nplanes > 4) throw new FitsException(FitsErrorKind.Unsupported, $"NAXIS3 = {nplanes}");
        double bzero = header.Double("BZERO") ?? 0, bscale = header.Double("BSCALE") ?? 1;
        int bytesPer = Math.Abs(bitpix) / 8;
        if (bytesPer == 0) throw new FitsException(FitsErrorKind.Unsupported, $"BITPIX = {bitpix}");
        long planeCount = (long)w * h;
        var planes = new List<PlanarImage>();
        var buf = new byte[Math.Min(planeCount * bytesPer, 4L << 20)];
        for (int p = 0; p < nplanes; p++)
        {
            var plane = PlanarImage.Uninitialized(w, h);
            long remaining = planeCount;
            long outIndex = 0;
            while (remaining > 0)
            {
                int cnt = (int)Math.Min(remaining, buf.Length / bytesPer);
                var chunk = buf.AsSpan(0, cnt * bytesPer);
                if (!ReadExactly(s, chunk)) throw new FitsException(FitsErrorKind.Truncated);
                Decode(chunk, cnt, bitpix, plane.Data.AsSpan((int)outIndex, cnt));
                remaining -= cnt; outIndex += cnt;
            }
            if (bscale != 1 || bzero != 0) Vec.ScaleAdd(plane.Data, (float)bscale, (float)bzero, plane.Data);
            // Replace NaN/Inf with 0 so that statistics never break.
            Vec.ZeroNonFinite(plane.Data);
            planes.Add(plane);
        }
        // Normalise integer data into [0,1] following the usual astro conventions.
        switch (bitpix)
        {
            case 8: foreach (var p in planes) p.Affine(1f / 255, 0); break;
            case 16:
            {
                // Unsigned 16-bit (BZERO = 32768) is the common case: value already in [0,65535].
                // Signed data without BZERO is shifted so the range maps to [0,1] as well.
                float shift = bzero == 0 ? 32768 : 0;
                foreach (var p in planes) p.Affine(1f / 65535, shift / 65535);
                break;
            }
            case 32:
            {
                double shift = bzero == 0 ? 2147483648.0 : 0;
                foreach (var p in planes) p.Affine((float)(1.0 / 4294967295.0), (float)(shift / 4294967295.0));
                break;
            }
        }
        if (string.Equals(header.String("ROWORDER"), "BOTTOM-UP", StringComparison.OrdinalIgnoreCase))
            foreach (var p in planes) p.FlipVertically();
        return new FitsImage(header, planes, bitpix);
    }

    static bool ReadExactly(Stream s, Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf[off..]);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    /// <summary>Returns true when the END card was found in this block.</summary>
    static bool ParseBlock(byte[] block, FitsHeader header)
    {
        for (int i = 0; i < BlockSize; i += 80)
        {
            var s = Encoding.Latin1.GetString(block, i, 80);
            if (s.StartsWith("END") && s[3..].All(c => c == ' ')) return true;
            var c = FitsHeader.ParseCard(s);
            if (c != null) header.Append(c.Value);
        }
        return false;
    }

    static void Decode(ReadOnlySpan<byte> raw, int count, int bitpix, Span<float> out_)
    {
        switch (bitpix)
        {
            case -32:
            {
                var src = MemoryMarshal.Cast<byte, uint>(raw[..(count * 4)]);
                var dst = MemoryMarshal.Cast<float, uint>(out_);
                BinaryPrimitives.ReverseEndianness(src, dst);
                break;
            }
            case -64:
                for (int i = 0; i < count; i++) out_[i] = (float)BinaryPrimitives.ReadDoubleBigEndian(raw.Slice(i * 8, 8));
                break;
            case 16:
                for (int i = 0; i < count; i++) out_[i] = BinaryPrimitives.ReadInt16BigEndian(raw.Slice(i * 2, 2));
                break;
            case 32:
                for (int i = 0; i < count; i++) out_[i] = BinaryPrimitives.ReadInt32BigEndian(raw.Slice(i * 4, 4));
                break;
            case 8:
                for (int i = 0; i < count; i++) out_[i] = raw[i];
                break;
            case 64:
                for (int i = 0; i < count; i++) out_[i] = BinaryPrimitives.ReadInt64BigEndian(raw.Slice(i * 8, 8));
                break;
            default:
                out_[..count].Clear();
                break;
        }
    }
}

public static class FitsWriter
{
    public enum Depth { Float32, UInt16 }

    /// <summary>Writes one or three planes as a primary HDU. Rows are written top-down (ROWORDER).</summary>
    public static void Write(IReadOnlyList<PlanarImage> planes, string path, Depth depth = Depth.Float32, IEnumerable<(string key, string value, string? comment)>? extraCards = null)
    {
        if (planes.Count == 0) throw new FitsException(FitsErrorKind.WriteFailed, "no planes");
        var first = planes[0];
        int w = first.Width, h = first.Height;
        var cards = new List<string>();
        void Card(string key, string value, string? comment = null)
        {
            var s = key.PadRight(8)[..8] + "= " + value.PadLeft(20);
            if (comment != null) s += " / " + comment;
            cards.Add(Fit80(s));
        }
        void StrCard(string key, string value, string? comment = null)
        {
            var v = "'" + value.Replace("'", "''").PadRight(Math.Max(8, value.Length)) + "'";
            var s = key.PadRight(8)[..8] + "= " + v;
            if (comment != null) s = s.PadRight(Math.Max(s.Length, 30)) + " / " + comment;
            cards.Add(Fit80(s));
        }
        Card("SIMPLE", "T", "CombineColors FITS");
        Card("BITPIX", depth == Depth.Float32 ? "-32" : "16", "bits per data element");
        Card("NAXIS", planes.Count > 1 ? "3" : "2");
        Card("NAXIS1", w.ToString(CultureInfo.InvariantCulture));
        Card("NAXIS2", h.ToString(CultureInfo.InvariantCulture));
        if (planes.Count > 1) Card("NAXIS3", planes.Count.ToString(CultureInfo.InvariantCulture));
        if (depth == Depth.UInt16)
        {
            Card("BZERO", "32768", "offset data range to that of unsigned short");
            Card("BSCALE", "1", "default scaling factor");
        }
        else
        {
            Card("BZERO", "0.0");
            Card("BSCALE", "1.0");
        }
        StrCard("ROWORDER", "TOP-DOWN", "Order of pixel rows stored in the image array");
        StrCard("SOFTWARE", "CombineColors");
        StrCard("DATE", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), "creation date");
        if (extraCards != null)
        {
            foreach (var (k, v, c) in extraCards)
            {
                if (v.StartsWith('\'')) cards.Add(Fit80(k.PadRight(8)[..8] + "= " + v + (c != null ? " / " + c : "")));
                else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || v == "T" || v == "F") Card(k, v, c);
                else StrCard(k, v, c);
            }
        }
        cards.Add(Fit80("END"));
        var headerString = new StringBuilder(string.Concat(cards));
        while (headerString.Length % FitsReader.BlockSize != 0) headerString.Append(' ');

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var hb = Encoding.Latin1.GetBytes(headerString.ToString());
            fs.Write(hb);
            long written = hb.Length;
            int n = w * h;
            if (depth == Depth.Float32)
            {
                var buf = new byte[Math.Min(n, 1 << 20) * 4];
                foreach (var p in planes)
                {
                    int off = 0;
                    while (off < n)
                    {
                        int cnt = Math.Min(n - off, buf.Length / 4);
                        var src = MemoryMarshal.Cast<float, uint>(p.Data.AsSpan(off, cnt));
                        var dst = MemoryMarshal.Cast<byte, uint>(buf.AsSpan(0, cnt * 4));
                        BinaryPrimitives.ReverseEndianness(src, dst);
                        fs.Write(buf, 0, cnt * 4);
                        off += cnt;
                    }
                    written += (long)n * 4;
                }
            }
            else
            {
                var buf = new byte[Math.Min(n, 1 << 20) * 2];
                foreach (var p in planes)
                {
                    int off = 0;
                    while (off < n)
                    {
                        int cnt = Math.Min(n - off, buf.Length / 2);
                        for (int i = 0; i < cnt; i++)
                        {
                            float v = MathF.Max(0, MathF.Min(65535, MathF.Round(p.Data[off + i] * 65535, MidpointRounding.AwayFromZero)));
                            int sv = (int)v - 32768;
                            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(i * 2, 2), (short)sv);
                        }
                        fs.Write(buf, 0, cnt * 2);
                        off += cnt;
                    }
                    written += (long)n * 2;
                }
            }
            long pad = (FitsReader.BlockSize - written % FitsReader.BlockSize) % FitsReader.BlockSize;
            if (pad > 0) fs.Write(new byte[pad]);
        }
        File.Move(tmp, path, true);
    }

    static string Fit80(string s) => (s.Length > 80 ? s[..80] : s).PadRight(80);
}
