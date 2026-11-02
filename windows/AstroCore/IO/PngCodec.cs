using System.Buffers.Binary;
using System.IO.Compression;

namespace AstroCore;

/// <summary>
/// Minimal, exact PNG reader / writer for the portable build (Windows keeps the Windows Imaging
/// Component). Reads 8- and 16-bit grayscale, grayscale + alpha, RGB and RGBA, non-interlaced;
/// anything else (palette, 1/2/4-bit, Adam7) is left to the platform codec at 8 bits. Writes 8- or
/// 16-bit grayscale / RGB. Samples are kept exactly as stored, no colour management.
/// </summary>
public static class PngCodec
{
    static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool LooksLikePng(byte[] data) => data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(Signature);

    public sealed class Decoded
    {
        public required PlanarImage[] Planes;   // 1 or 3
        public required int BitsPerComponent;   // 8 or 16
    }

    /// <summary>Returns null for PNG variants this reader does not handle (the caller falls back to the platform codec).</summary>
    public static Decoded? TryDecode(byte[] data)
    {
        if (!LooksLikePng(data)) throw new ImageIOException(ImageIOErrorKind.CannotDecode, "not a PNG file");
        int pos = 8, w = 0, h = 0, depth = 0, colorType = 0, interlace = 0;
        var idat = new MemoryStream();
        bool header = false;
        while (pos + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4));
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            pos += 8;
            if (len < 0 || pos + len + 4 > data.Length) throw new ImageIOException(ImageIOErrorKind.CannotDecode, "truncated PNG");
            var body = data.AsSpan(pos, len);
            switch (type)
            {
                case "IHDR":
                    if (len < 13) throw new ImageIOException(ImageIOErrorKind.CannotDecode, "bad IHDR");
                    w = BinaryPrimitives.ReadInt32BigEndian(body); h = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8]; colorType = body[9]; interlace = body[12];
                    header = true;
                    break;
                case "IDAT": idat.Write(body); break;
                case "IEND": pos = data.Length; break;
            }
            pos += len + 4;   // data + CRC (not verified)
        }
        if (!header || w <= 0 || h <= 0) throw new ImageIOException(ImageIOErrorKind.CannotDecode, "missing IHDR");
        if (interlace != 0 || (depth != 8 && depth != 16) || colorType is not (0 or 2 or 4 or 6)) return null;
        int comps = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        int bytesPerSample = depth / 8, bpp = comps * bytesPerSample, stride = w * bpp;
        var raw = new byte[(long)(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length) { int n = z.Read(raw, read, raw.Length - read); if (n <= 0) break; read += n; }
            if (read < raw.Length) throw new ImageIOException(ImageIOErrorKind.CannotDecode, "PNG image data is incomplete");
        }
        // Undo the per-row filters in place (rows become contiguous at offset y * stride, the filter bytes are skipped).
        var pix = new byte[(long)stride * h];
        var prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int filter = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var dst = pix.AsSpan(y * stride, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? dst[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int pred = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new ImageIOException(ImageIOErrorKind.CannotDecode, $"unknown PNG filter {filter}"),
                };
                dst[i] = (byte)(src[i] + pred);
            }
            dst.CopyTo(prev);
        }
        int channels = comps >= 3 ? 3 : 1;
        var planes = new PlanarImage[channels];
        for (int c = 0; c < channels; c++) planes[c] = PlanarImage.Uninitialized(w, h);
        float scale = depth == 16 ? 1f / 65535 : 1f / 255;
        Par.Rows(h, (long)w * channels, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
                for (int c = 0; c < channels; c++)
                {
                    var dst = planes[c].Data.AsSpan(y * w, w);
                    int row = y * stride;
                    if (depth == 16) for (int x = 0; x < w; x++) dst[x] = BinaryPrimitives.ReadUInt16BigEndian(pix.AsSpan(row + (x * comps + c) * 2, 2)) * scale;
                    else for (int x = 0; x < w; x++) dst[x] = pix[row + x * comps + c] * scale;
                }
        });
        return new Decoded { Planes = planes, BitsPerComponent = depth };
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    /// <summary>Encodes 1 (gray) or 3 (RGB) planes at 8 or 16 bits; values are clamped to [0,1] and rounded.</summary>
    public static void Encode(IReadOnlyList<PlanarImage> planes, int bitsPerComponent, Stream output)
    {
        int w = planes[0].Width, h = planes[0].Height;
        int channels = planes.Count == 1 ? 1 : 3;
        int bytesPer = bitsPerComponent == 16 ? 2 : 1, bpp = channels * bytesPer, stride = w * bpp;
        float scale = bitsPerComponent == 16 ? 65535 : 255;
        var raw = new byte[(long)(stride + 1) * h];
        var tmp = new float[w];
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            for (int c = 0; c < channels; c++)
            {
                Vec.Clip(planes[c].Data.AsSpan(y * w, w), 0, 1, tmp);
                if (bytesPer == 2)
                    for (int x = 0; x < w; x++) BinaryPrimitives.WriteUInt16BigEndian(cur.AsSpan((x * channels + c) * 2, 2), (ushort)MathF.Round(tmp[x] * scale, MidpointRounding.AwayFromZero));
                else
                    for (int x = 0; x < w; x++) cur[x * channels + c] = (byte)MathF.Round(tmp[x] * scale, MidpointRounding.AwayFromZero);
            }
            // "Up" filter: cheap, and it helps the deflate on smooth images.
            int o = y * (stride + 1);
            raw[o] = 2;
            for (int i = 0; i < stride; i++) raw[o + 1 + i] = (byte)(cur[i] - prev[i]);
            (prev, cur) = (cur, prev);
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
        output.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)bitsPerComponent; ihdr[9] = (byte)(channels == 1 ? 0 : 2);
        Chunk(output, "IHDR", ihdr);
        Chunk(output, "IDAT", z.GetBuffer().AsSpan(0, (int)z.Length));
        Chunk(output, "IEND", []);
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
        s.Write(len);
        Span<byte> t = stackalloc byte[4];
        for (int i = 0; i < 4; i++) t[i] = (byte)type[i];
        s.Write(t);
        s.Write(body);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, t), body) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    static class Crc32
    {
        static readonly uint[] Table = Build();
        static uint[] Build()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
