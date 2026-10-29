using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace AstroCore;

/// <summary>
/// A self-contained baseline TIFF reader for the files astrophotography tools exchange: 8/16/32-bit
/// integer and 32/64-bit float samples, 1–4 samples per pixel (chunky or planar), strips or tiles,
/// uncompressed / LZW / Deflate / PackBits, horizontal and floating-point predictors, classic and
/// BigTIFF. Values are returned exactly as stored (integer codes divided by their maximum, floats
/// untouched) and no colour management is applied — the Windows Imaging Component is used as a
/// fallback for anything else (see <see cref="RasterReader"/>).
/// </summary>
public static class TiffReader
{
    public sealed class Result
    {
        public required IReadOnlyList<PlanarImage> Planes { get; init; }
        public required int BitsPerSample { get; init; }
        public required bool IsFloat { get; init; }
    }

    sealed class Ifd
    {
        public readonly Dictionary<int, Entry> Entries = new();
        public Reader? reader;
    }

    readonly record struct Entry(int Tag, int Type, long Count, long ValueOffset, byte[] Inline);

    public static bool LooksLikeTiff(ReadOnlySpan<byte> head)
        => head.Length >= 4 && ((head[0] == 'I' && head[1] == 'I' && head[2] == 42 && head[3] == 0) || (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] == 42)
                              || (head[0] == 'I' && head[1] == 'I' && head[2] == 43 && head[3] == 0) || (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] == 43));

    public static Result Read(string path) => Read(File.ReadAllBytes(path));

    public static Result Read(byte[] data)
    {
        if (data.Length < 8) throw new ImageIOException(ImageIOErrorKind.CannotDecode);
        bool little = data[0] == 'I' && data[1] == 'I';
        if (!(little || (data[0] == 'M' && data[1] == 'M'))) throw new ImageIOException(ImageIOErrorKind.CannotDecode);
        var r = new Reader(data, little);
        int version = r.U16(2);
        bool big = version == 43;
        if (version != 42 && !big) throw new ImageIOException(ImageIOErrorKind.CannotDecode);
        long ifdOffset;
        if (big)
        {
            if (r.U16(4) != 8) throw new ImageIOException(ImageIOErrorKind.UnsupportedFormat, "BigTIFF offset size");
            ifdOffset = (long)r.U64(8);
        }
        else ifdOffset = r.U32(4);
        var ifd = ReadIfd(r, ifdOffset, big);

        int width = (int)ifd.Long(256, 0), height = (int)ifd.Long(257, 0);
        if (width <= 0 || height <= 0) throw new ImageIOException(ImageIOErrorKind.CannotDecode);
        int spp = (int)ifd.Long(277, 1);
        var bpsArr = ifd.Longs(258);
        int bps = bpsArr.Length > 0 ? (int)bpsArr[0] : 1;
        int compression = (int)ifd.Long(259, 1);
        int photometric = (int)ifd.Long(262, spp >= 3 ? 2 : 1);
        int planar = (int)ifd.Long(284, 1);
        int predictor = (int)ifd.Long(317, 1);
        var sfArr = ifd.Longs(339);
        int sampleFormat = sfArr.Length > 0 ? (int)sfArr[0] : 1;
        if (bps != 8 && bps != 16 && bps != 32 && bps != 64) throw new ImageIOException(ImageIOErrorKind.UnsupportedFormat, $"{bps} bits per sample");
        if (sampleFormat == 3 && bps != 32 && bps != 64) throw new ImageIOException(ImageIOErrorKind.UnsupportedFormat, $"{bps}-bit float");
        if (sampleFormat != 1 && sampleFormat != 2 && sampleFormat != 3) throw new ImageIOException(ImageIOErrorKind.UnsupportedFormat, $"sample format {sampleFormat}");
        if (compression != 1 && compression != 5 && compression != 8 && compression != 32946 && compression != 32773)
            throw new ImageIOException(ImageIOErrorKind.UnsupportedFormat, $"TIFF compression {compression}");
        if (photometric == 3 || photometric == 5 || photometric == 6) throw new ImageIOException(ImageIOErrorKind.UnsupportedFormat, $"photometric {photometric}");
        int bytesPer = bps / 8;

        // Decoded, predictor-undone sample buffer per plane in native byte order (little-endian).
        int outPlanes = Math.Min(spp, spp >= 3 ? 3 : 1);
        var planes = new PlanarImage[outPlanes];
        for (int c = 0; c < outPlanes; c++) planes[c] = PlanarImage.Uninitialized(width, height);

        bool tiled = ifd.Entries.ContainsKey(324);
        long[] offsets = ifd.Longs(tiled ? 324 : 273), counts = ifd.Longs(tiled ? 325 : 279);
        if (offsets.Length == 0) throw new ImageIOException(ImageIOErrorKind.CannotDecode);
        int tileW = tiled ? (int)ifd.Long(322, width) : width;
        int tileH = tiled ? (int)ifd.Long(323, height) : (int)Math.Min(ifd.Long(278, height), height);
        if (tileH <= 0) tileH = height;
        int tilesAcross = tiled ? (width + tileW - 1) / tileW : 1;
        int tilesDown = (height + tileH - 1) / tileH;
        int planesStored = planar == 2 ? spp : 1;
        int sppInChunk = planar == 2 ? 1 : spp;
        int tilesPerPlane = tilesAcross * tilesDown;

        Parallel.For(0, Math.Min(offsets.Length, tilesPerPlane * planesStored), t =>
        {
            int planeIdx = planar == 2 ? t / tilesPerPlane : 0;
            int ti = t % tilesPerPlane;
            int tx = ti % tilesAcross, ty = ti / tilesAcross;
            int x0 = tx * tileW, y0 = ty * tileH;
            int tw = tiled ? tileW : width, th = Math.Min(tileH, height - y0);
            if (th <= 0) return;
            int rowBytes = tw * sppInChunk * bytesPer;
            long expected = (long)rowBytes * (tiled ? tileH : th);
            long cnt = t < counts.Length ? counts[t] : data.Length - offsets[t];
            if (offsets[t] < 0 || offsets[t] + cnt > data.Length) throw new ImageIOException(ImageIOErrorKind.CannotDecode);
            byte[] raw = Decompress(data, (int)offsets[t], (int)cnt, compression, expected);
            if (raw.Length < expected)
            {
                var padded = new byte[expected];
                Array.Copy(raw, padded, raw.Length);
                raw = padded;
            }
            int rowsInChunk = tiled ? tileH : th;
            if (predictor == 2) UndoHorizontal(raw, rowsInChunk, tw, sppInChunk, bytesPer, little);
            else if (predictor == 3) UndoFloating(raw, rowsInChunk, tw, sppInChunk, bytesPer);
            bool le = predictor == 3 || little;
            int visibleW = Math.Min(tw, width - x0);
            for (int y = 0; y < th; y++)
            {
                int rowOff = y * rowBytes;
                for (int c = 0; c < sppInChunk; c++)
                {
                    int outPlane = planar == 2 ? planeIdx : c;
                    if (outPlane >= outPlanes) continue;
                    var dst = planes[outPlane].Data.AsSpan((y0 + y) * width + x0, visibleW);
                    DecodeRow(raw, rowOff + c * bytesPer, sppInChunk * bytesPer, visibleW, bps, sampleFormat, le, dst);
                }
            }
        });

        if (photometric == 0 && sampleFormat != 3)
            foreach (var p in planes) Vec.ScaleAdd(p.Data, -1, 1, p.Data);
        foreach (var p in planes) Vec.ZeroNonFinite(p.Data);
        return new Result { Planes = planes, BitsPerSample = bps, IsFloat = sampleFormat == 3 };
    }

    static void DecodeRow(byte[] raw, int off, int stride, int count, int bps, int sampleFormat, bool little, Span<float> dst)
    {
        switch (bps)
        {
            case 8:
                if (sampleFormat == 2) for (int i = 0; i < count; i++) dst[i] = ((sbyte)raw[off + i * stride] + 128) / 255f;
                else for (int i = 0; i < count; i++) dst[i] = raw[off + i * stride] / 255f;
                break;
            case 16:
                for (int i = 0; i < count; i++)
                {
                    var s = raw.AsSpan(off + i * stride, 2);
                    ushort u = little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
                    dst[i] = sampleFormat == 2 ? ((short)u + 32768) / 65535f : u / 65535f;
                }
                break;
            case 32:
                for (int i = 0; i < count; i++)
                {
                    var s = raw.AsSpan(off + i * stride, 4);
                    uint u = little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
                    if (sampleFormat == 3) dst[i] = BitConverter.UInt32BitsToSingle(u);
                    else if (sampleFormat == 2) dst[i] = (float)(((int)u + 2147483648.0) / 4294967295.0);
                    else dst[i] = (float)(u / 4294967295.0);
                }
                break;
            case 64:
                for (int i = 0; i < count; i++)
                {
                    var s = raw.AsSpan(off + i * stride, 8);
                    ulong u = little ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s);
                    if (sampleFormat == 3) dst[i] = (float)BitConverter.UInt64BitsToDouble(u);
                    else if (sampleFormat == 2) dst[i] = (float)(((long)u + 9223372036854775808.0) / 18446744073709551615.0);
                    else dst[i] = (float)(u / 18446744073709551615.0);
                }
                break;
        }
    }

    static void UndoHorizontal(byte[] raw, int rows, int w, int spp, int bytesPer, bool little)
    {
        int rowBytes = w * spp * bytesPer;
        for (int y = 0; y < rows; y++)
        {
            int o = y * rowBytes;
            if (o + rowBytes > raw.Length) break;
            switch (bytesPer)
            {
                case 1:
                    for (int i = spp; i < rowBytes; i++) raw[o + i] = (byte)(raw[o + i] + raw[o + i - spp]);
                    break;
                case 2:
                    for (int i = spp; i < w * spp; i++)
                    {
                        var cur = raw.AsSpan(o + i * 2, 2); var prev = raw.AsSpan(o + (i - spp) * 2, 2);
                        if (little) BinaryPrimitives.WriteUInt16LittleEndian(cur, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(cur) + BinaryPrimitives.ReadUInt16LittleEndian(prev)));
                        else BinaryPrimitives.WriteUInt16BigEndian(cur, (ushort)(BinaryPrimitives.ReadUInt16BigEndian(cur) + BinaryPrimitives.ReadUInt16BigEndian(prev)));
                    }
                    break;
                case 4:
                    for (int i = spp; i < w * spp; i++)
                    {
                        var cur = raw.AsSpan(o + i * 4, 4); var prev = raw.AsSpan(o + (i - spp) * 4, 4);
                        if (little) BinaryPrimitives.WriteUInt32LittleEndian(cur, BinaryPrimitives.ReadUInt32LittleEndian(cur) + BinaryPrimitives.ReadUInt32LittleEndian(prev));
                        else BinaryPrimitives.WriteUInt32BigEndian(cur, BinaryPrimitives.ReadUInt32BigEndian(cur) + BinaryPrimitives.ReadUInt32BigEndian(prev));
                    }
                    break;
            }
        }
    }

    /// <summary>Floating-point predictor (TIFF Technical Note 3): byte-wise differencing of byte planes; output little-endian.</summary>
    static void UndoFloating(byte[] raw, int rows, int w, int spp, int bytesPer)
    {
        int wc = w * spp, rowBytes = wc * bytesPer;
        var tmp = new byte[rowBytes];
        for (int y = 0; y < rows; y++)
        {
            int o = y * rowBytes;
            if (o + rowBytes > raw.Length) break;
            for (int i = spp; i < rowBytes; i++) raw[o + i] = (byte)(raw[o + i] + raw[o + i - spp]);
            Array.Copy(raw, o, tmp, 0, rowBytes);
            for (int i = 0; i < wc; i++)
                for (int b = 0; b < bytesPer; b++)
                    raw[o + i * bytesPer + b] = tmp[(bytesPer - b - 1) * wc + i];
        }
    }

    static byte[] Decompress(byte[] data, int off, int cnt, int compression, long expected)
    {
        switch (compression)
        {
            case 1:
            {
                var o = new byte[cnt];
                Array.Copy(data, off, o, 0, cnt);
                return o;
            }
            case 5: return Lzw.Decode(data.AsSpan(off, cnt), expected);
            case 8:
            case 32946:
            {
                using var ms = new MemoryStream(data, off, cnt, false);
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                using var o = new MemoryStream((int)Math.Min(expected, int.MaxValue / 2));
                z.CopyTo(o);
                return o.ToArray();
            }
            case 32773: return PackBits(data.AsSpan(off, cnt), expected);
            default: throw new ImageIOException(ImageIOErrorKind.UnsupportedFormat, $"TIFF compression {compression}");
        }
    }

    static byte[] PackBits(ReadOnlySpan<byte> src, long expected)
    {
        var o = new List<byte>((int)expected);
        int i = 0;
        while (i < src.Length && o.Count < expected)
        {
            sbyte n = (sbyte)src[i++];
            if (n >= 0) { for (int k = 0; k <= n && i < src.Length; k++) o.Add(src[i++]); }
            else if (n != -128) { if (i < src.Length) { byte b = src[i++]; for (int k = 0; k < 1 - n; k++) o.Add(b); } }
        }
        return o.ToArray();
    }

    /// <summary>TIFF-flavoured LZW (MSB-first codes, early change).</summary>
    static class Lzw
    {
        public static byte[] Decode(ReadOnlySpan<byte> src, long expected)
        {
            var o = new byte[expected];
            long op = 0;
            var prefix = new int[4096]; var suffix = new byte[4096]; var length = new int[4096];
            for (int i = 0; i < 256; i++) { prefix[i] = -1; suffix[i] = (byte)i; length[i] = 1; }
            int codeBits = 9, next = 258, prev = -1;
            long bitPos = 0; long totalBits = (long)src.Length * 8;
            var stack = new byte[4097];
            while (bitPos + codeBits <= totalBits && op < expected)
            {
                int code = ReadCode(src, bitPos, codeBits); bitPos += codeBits;
                if (code == 256) { codeBits = 9; next = 258; prev = -1; continue; }
                if (code == 257) break;
                int entryLen;
                if (code < next)
                {
                    entryLen = Emit(code, prefix, suffix, length, stack);
                    if (prev >= 0 && next < 4096) { prefix[next] = prev; suffix[next] = stack[0]; length[next] = length[prev] + 1; next++; }
                }
                else if (code == next && prev >= 0)
                {
                    int l = Emit(prev, prefix, suffix, length, stack);
                    stack[l] = stack[0]; entryLen = l + 1;
                    if (next < 4096) { prefix[next] = prev; suffix[next] = stack[0]; length[next] = length[prev] + 1; next++; }
                }
                else throw new ImageIOException(ImageIOErrorKind.CannotDecode, "LZW");
                long n = Math.Min(entryLen, expected - op);
                Array.Copy(stack, 0, o, op, n); op += n;
                prev = code;
                if (next + 1 >= (1 << codeBits) && codeBits < 12) codeBits++;
            }
            return o;
        }

        static int Emit(int code, int[] prefix, byte[] suffix, int[] length, byte[] stack)
        {
            int l = length[code];
            int i = l - 1, c = code;
            while (c >= 0 && i >= 0) { stack[i--] = suffix[c]; c = prefix[c]; }
            return l;
        }

        static int ReadCode(ReadOnlySpan<byte> src, long bitPos, int bits)
        {
            int v = 0;
            for (int i = 0; i < bits; i++)
            {
                long p = bitPos + i;
                int bit = (src[(int)(p >> 3)] >> (7 - (int)(p & 7))) & 1;
                v = (v << 1) | bit;
            }
            return v;
        }
    }

    sealed class Reader
    {
        readonly byte[] d; readonly bool le;
        public Reader(byte[] data, bool little) { d = data; le = little; }
        public bool LittleEndian => le;
        public ushort U16(long o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)o, 2)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)o, 2));
        public uint U32(long o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)o, 4)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)o, 4));
        public ulong U64(long o) => le ? BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)o, 8)) : BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan((int)o, 8));
        public int Length => d.Length;
        public byte[] Bytes(long o, int n) { var b = new byte[n]; Array.Copy(d, o, b, 0, n); return b; }
    }

    static int TypeSize(int type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 or 16 or 17 or 18 => 8, _ => 1 };

    static Ifd ReadIfd(Reader r, long offset, bool big)
    {
        var ifd = new Ifd();
        if (offset <= 0 || offset + (big ? 8 : 2) > r.Length) throw new ImageIOException(ImageIOErrorKind.CannotDecode);
        long count = big ? (long)r.U64(offset) : r.U16(offset);
        long p = offset + (big ? 8 : 2);
        int entrySize = big ? 20 : 12;
        for (long i = 0; i < count; i++, p += entrySize)
        {
            if (p + entrySize > r.Length) break;
            int tag = r.U16(p), type = r.U16(p + 2);
            long n = big ? (long)r.U64(p + 4) : r.U32(p + 4);
            int inlineBytes = big ? 8 : 4;
            long valueField = p + (big ? 12 : 8);
            long size = n * TypeSize(type);
            byte[] inlineData;
            long valueOffset;
            if (size <= inlineBytes) { inlineData = r.Bytes(valueField, (int)size); valueOffset = valueField; }
            else { valueOffset = big ? (long)r.U64(valueField) : r.U32(valueField); inlineData = valueOffset + size <= r.Length && valueOffset >= 0 ? r.Bytes(valueOffset, (int)size) : []; }
            ifd.Entries[tag] = new Entry(tag, type, n, valueOffset, inlineData);
        }
        ifd.reader = r;
        return ifd;
    }

    static long[] LongsOf(Entry e, Reader r)
    {
        var out_ = new long[e.Count];
        int sz = TypeSize(e.Type);
        for (long i = 0; i < e.Count; i++)
        {
            int o = (int)(i * sz);
            if (o + sz > e.Inline.Length) break;
            var s = e.Inline.AsSpan(o, sz);
            out_[i] = e.Type switch
            {
                1 or 7 => s[0],
                3 => r.LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s),
                4 or 13 => r.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s),
                16 or 18 => (long)(r.LittleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s)),
                8 => r.LittleEndian ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadInt16BigEndian(s),
                9 => r.LittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s),
                17 => r.LittleEndian ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s),
                _ => 0,
            };
        }
        return out_;
    }

    static long Long(this Ifd ifd, int tag, long fallback)
    {
        if (!ifd.Entries.TryGetValue(tag, out var e)) return fallback;
        var v = LongsOf(e, ifd.reader!);
        return v.Length > 0 ? v[0] : fallback;
    }

    static long[] Longs(this Ifd ifd, int tag) => ifd.Entries.TryGetValue(tag, out var e) ? LongsOf(e, ifd.reader!) : [];
}

public static class TiffWriter
{
    public enum SampleType { UInt16, Float32 }

    /// <summary>Writes 1 or 3 planes as an uncompressed little-endian baseline TIFF (chunky, strips of about 1 MB).</summary>
    public static void Write(IReadOnlyList<PlanarImage> planes, string path, SampleType type)
    {
        int w = planes[0].Width, h = planes[0].Height;
        int spp = planes.Count >= 3 ? 3 : 1;
        int bytesPer = type == SampleType.UInt16 ? 2 : 4;
        int rowBytes = w * spp * bytesPer;
        int rowsPerStrip = Math.Max(1, Math.Min(h, (1 << 20) / Math.Max(1, rowBytes)));
        int strips = (h + rowsPerStrip - 1) / rowsPerStrip;
        long dataSize = (long)rowBytes * h;
        if (8 + dataSize > uint.MaxValue - (1 << 16)) throw new ImageIOException(ImageIOErrorKind.CannotWrite, "image too large for classic TIFF");

        var tags = new List<(int tag, int type, int count, byte[] value)>();
        void Short(int tag, params int[] v) { var b = new byte[2 * v.Length]; for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)v[i]); tags.Add((tag, 3, v.Length, b)); }
        void Long(int tag, params uint[] v) { var b = new byte[4 * v.Length]; for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]); tags.Add((tag, 4, v.Length, b)); }
        void Ascii(int tag, string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); tags.Add((tag, 2, b.Length, b)); }

        const int headerSize = 8;
        var stripOffsets = new uint[strips]; var stripCounts = new uint[strips];
        long pos = headerSize;
        for (int s = 0; s < strips; s++)
        {
            int rows = Math.Min(rowsPerStrip, h - s * rowsPerStrip);
            stripOffsets[s] = (uint)pos; stripCounts[s] = (uint)(rows * rowBytes); pos += stripCounts[s];
        }
        if (pos % 2 == 1) pos++;
        uint ifdOffset = (uint)pos;

        Long(256, (uint)w);
        Long(257, (uint)h);
        Short(258, Enumerable.Repeat(bytesPer * 8, spp).ToArray());
        Short(259, 1);
        Short(262, spp == 3 ? 2 : 1);
        Long(273, stripOffsets);
        Short(277, spp);
        Long(278, (uint)rowsPerStrip);
        Long(279, stripCounts);
        Short(284, 1);
        Ascii(305, "CombineColors");
        Short(339, Enumerable.Repeat(type == SampleType.Float32 ? 3 : 1, spp).ToArray());

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            fs.Write("II"u8); fs.Write(new byte[] { 42, 0 });
            var ofs = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(ofs, ifdOffset); fs.Write(ofs);
            var row = new byte[rowBytes];
            var rowFloats = new float[w];
            for (int y = 0; y < h; y++)
            {
                for (int c = 0; c < spp; c++)
                {
                    var src = planes[c].Data.AsSpan(y * w, w);
                    if (type == SampleType.UInt16)
                    {
                        Vec.Clip(src, 0, 1, rowFloats);
                        for (int x = 0; x < w; x++)
                            BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan((x * spp + c) * 2), (ushort)MathF.Round(rowFloats[x] * 65535, MidpointRounding.AwayFromZero));
                    }
                    else
                    {
                        for (int x = 0; x < w; x++) BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan((x * spp + c) * 4), src[x]);
                    }
                }
                fs.Write(row);
            }
            if (fs.Position % 2 == 1) fs.WriteByte(0);
            // IFD
            var count = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)tags.Count); fs.Write(count);
            long extraPos = ifdOffset + 2 + tags.Count * 12 + 4;
            var extra = new MemoryStream();
            foreach (var (tag, type_, n, value) in tags.OrderBy(t => t.tag))
            {
                var e = new byte[12];
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(0), (ushort)tag);
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), (ushort)type_);
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), (uint)n);
                if (value.Length <= 4) Array.Copy(value, 0, e, 8, value.Length);
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), (uint)(extraPos + extra.Length));
                    extra.Write(value);
                    if (extra.Length % 2 == 1) extra.WriteByte(0);
                }
                fs.Write(e);
            }
            fs.Write(new byte[4]);
            fs.Write(extra.ToArray());
        }
        File.Move(tmp, path, true);
    }
}
