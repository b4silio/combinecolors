using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstroCore;

public enum ImageIOErrorKind { CannotOpen, CannotDecode, CannotWrite, UnsupportedFormat }

public sealed class ImageIOException : Exception
{
    public ImageIOErrorKind Kind { get; }
    public ImageIOException(ImageIOErrorKind kind, string? detail = null) : base(Describe(kind, detail)) { Kind = kind; }

    static string Describe(ImageIOErrorKind k, string? d) => k switch
    {
        ImageIOErrorKind.CannotOpen => "The image could not be opened.",
        ImageIOErrorKind.CannotDecode => d == null ? "The image could not be decoded." : $"The image could not be decoded ({d}).",
        ImageIOErrorKind.CannotWrite => $"The image could not be written: {d}",
        _ => $"Unsupported image format: {d}",
    };
}

/// <summary>
/// Reading TIFF / PNG / JPEG into float planes. TIFF goes through the built-in reader (exact values,
/// no colour management); PNG / JPEG and any TIFF it cannot handle go through the Windows Imaging
/// Component with the pixel format preserved. Values are returned exactly as stored (integer codes
/// divided by their maximum, floats untouched).
/// </summary>
public static class RasterReader
{
    public sealed class Result
    {
        public required IReadOnlyList<PlanarImage> Planes { get; init; }   // 1 (gray) or 3 (RGB)
        public required int BitsPerComponent { get; init; }
        public required bool IsFloat { get; init; }
    }

    public static Result Read(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) { throw new ImageIOException(ImageIOErrorKind.CannotOpen, e.Message); }
        if (TiffReader.LooksLikeTiff(data))
        {
            try
            {
                var t = TiffReader.Read(data);
                return new Result { Planes = t.Planes, BitsPerComponent = t.BitsPerSample, IsFloat = t.IsFloat };
            }
            catch (ImageIOException e) when (e.Kind == ImageIOErrorKind.UnsupportedFormat) { /* fall through to WIC */ }
        }
        return ReadWithWic(data);
    }

    public static Result ReadWithWic(byte[] data)
    {
        BitmapFrame frame;
        try
        {
            using var ms = new MemoryStream(data, false);
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (dec.Frames.Count == 0) throw new ImageIOException(ImageIOErrorKind.CannotDecode);
            frame = dec.Frames[0];
        }
        catch (ImageIOException) { throw; }
        catch (Exception e) { throw new ImageIOException(ImageIOErrorKind.CannotDecode, e.Message); }
        return FromBitmapSource(frame);
    }

    /// <summary>Converts any WIC bitmap into float planes without colour conversion.</summary>
    public static Result FromBitmapSource(BitmapSource src)
    {
        var fmt = src.Format;
        int w = src.PixelWidth, h = src.PixelHeight;
        // Formats we decode ourselves; everything else is converted to a close integer format first.
        if (!(fmt == PixelFormats.Gray8 || fmt == PixelFormats.Gray16 || fmt == PixelFormats.Rgb24 || fmt == PixelFormats.Bgr24 || fmt == PixelFormats.Bgr32
              || fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32 || fmt == PixelFormats.Rgb48 || fmt == PixelFormats.Rgba64 || fmt == PixelFormats.Prgba64
              || fmt == PixelFormats.Gray32Float || fmt == PixelFormats.Rgb128Float || fmt == PixelFormats.Rgba128Float || fmt == PixelFormats.Prgba128Float))
        {
            var target = fmt.BitsPerPixel > 32 ? PixelFormats.Rgb48 : (fmt == PixelFormats.BlackWhite || fmt == PixelFormats.Gray2 || fmt == PixelFormats.Gray4 ? PixelFormats.Gray8 : PixelFormats.Bgr24);
            var conv = new FormatConvertedBitmap(src, target, null, 0);
            conv.Freeze();
            src = conv; fmt = target;
        }
        int bpp = fmt.BitsPerPixel;
        int stride = (w * bpp + 7) / 8;
        var bytes = new byte[(long)stride * h];
        src.CopyPixels(bytes, stride, 0);
        int channels, bits; bool isFloat = false;
        if (fmt == PixelFormats.Gray8) { channels = 1; bits = 8; }
        else if (fmt == PixelFormats.Gray16) { channels = 1; bits = 16; }
        else if (fmt == PixelFormats.Gray32Float) { channels = 1; bits = 32; isFloat = true; }
        else if (fmt == PixelFormats.Rgb24 || fmt == PixelFormats.Bgr24 || fmt == PixelFormats.Bgr32 || fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32) { channels = 3; bits = 8; }
        else if (fmt == PixelFormats.Rgb48 || fmt == PixelFormats.Rgba64 || fmt == PixelFormats.Prgba64) { channels = 3; bits = 16; }
        else { channels = 3; bits = 32; isFloat = true; }
        bool bgr = fmt == PixelFormats.Bgr24 || fmt == PixelFormats.Bgr32 || fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32;
        int comps = bpp / bits;   // components stored per pixel (incl. alpha / padding)
        var planes = new PlanarImage[channels];
        for (int c = 0; c < channels; c++) planes[c] = PlanarImage.Uninitialized(w, h);
        Par.Rows(h, (long)w * channels, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int c = 0; c < channels; c++)
                {
                    int sc = bgr ? 2 - c : c;
                    var dst = planes[c].Data.AsSpan(y * w, w);
                    switch (bits)
                    {
                        case 8: for (int x = 0; x < w; x++) dst[x] = bytes[row + x * comps + sc] / 255f; break;
                        case 16: for (int x = 0; x < w; x++) dst[x] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(row + (x * comps + sc) * 2, 2)) / 65535f; break;
                        default: for (int x = 0; x < w; x++) dst[x] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(row + (x * comps + sc) * 4, 4)); break;
                    }
                }
            }
        });
        foreach (var p in planes) Vec.ZeroNonFinite(p.Data);
        return new Result { Planes = planes, BitsPerComponent = bits, IsFloat = isFloat };
    }
}

/// <summary>Output file formats.</summary>
public enum RasterFormat { Tiff16, Tiff32, Png16, Png8, Jpeg, Fits32, Fits16 }

public static class RasterFormats
{
    public static readonly RasterFormat[] All = [RasterFormat.Tiff16, RasterFormat.Tiff32, RasterFormat.Png16, RasterFormat.Png8, RasterFormat.Jpeg, RasterFormat.Fits32, RasterFormat.Fits16];

    /// <summary>User-facing name (also the identifier stored in preferences).</summary>
    public static string RawValue(this RasterFormat f) => f switch
    {
        RasterFormat.Tiff16 => "TIFF 16-bit",
        RasterFormat.Tiff32 => "TIFF 32-bit float",
        RasterFormat.Png16 => "PNG 16-bit",
        RasterFormat.Png8 => "PNG 8-bit",
        RasterFormat.Jpeg => "JPEG",
        RasterFormat.Fits32 => "FITS 32-bit float",
        _ => "FITS 16-bit",
    };

    public static RasterFormat? FromRawValue(string? s) => All.FirstOrDefault(f => f.RawValue() == s, (RasterFormat)(-1)) is var f && (int)f >= 0 ? f : null;

    public static string FileExtension(this RasterFormat f) => f switch
    {
        RasterFormat.Tiff16 or RasterFormat.Tiff32 => "tif",
        RasterFormat.Png16 or RasterFormat.Png8 => "png",
        RasterFormat.Jpeg => "jpg",
        _ => "fits",
    };

    public static bool IsFits(this RasterFormat f) => f == RasterFormat.Fits32 || f == RasterFormat.Fits16;

    /// <summary>Open/save dialog filter string for the format.</summary>
    public static string DialogFilter(this RasterFormat f) => f switch
    {
        RasterFormat.Tiff16 or RasterFormat.Tiff32 => "TIFF image|*.tif;*.tiff",
        RasterFormat.Png16 or RasterFormat.Png8 => "PNG image|*.png",
        RasterFormat.Jpeg => "JPEG image|*.jpg;*.jpeg",
        _ => "FITS image|*.fits;*.fit;*.fts",
    };
}

/// <summary>Export helpers and display-bitmap conversion.</summary>
public static class RasterWriter
{
    /// <summary>Builds a WIC bitmap from 1 or 3 float planes with the requested depth (values clamped to [0,1]).</summary>
    public static BitmapSource MakeBitmap(IReadOnlyList<PlanarImage> planes, int bitsPerComponent)
    {
        int w = planes[0].Width, h = planes[0].Height;
        int channels = planes.Count == 1 ? 1 : 3;
        PixelFormat fmt = channels == 1 ? (bitsPerComponent == 16 ? PixelFormats.Gray16 : PixelFormats.Gray8)
                                        : (bitsPerComponent == 16 ? PixelFormats.Rgb48 : PixelFormats.Rgb24);
        int bytesPer = bitsPerComponent == 16 ? 2 : 1;
        int stride = w * channels * bytesPer;
        var bytes = new byte[(long)stride * h];
        var tmp = new float[w];
        float scale = bitsPerComponent == 16 ? 65535 : 255;
        for (int y = 0; y < h; y++)
        {
            for (int c = 0; c < channels; c++)
            {
                Vec.Clip(planes[c].Data.AsSpan(y * w, w), 0, 1, tmp);
                int row = y * stride;
                if (bytesPer == 2)
                    for (int x = 0; x < w; x++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(row + (x * channels + c) * 2, 2), (ushort)MathF.Round(tmp[x] * scale, MidpointRounding.AwayFromZero));
                else
                    for (int x = 0; x < w; x++) bytes[row + x * channels + c] = (byte)MathF.Round(tmp[x] * scale, MidpointRounding.AwayFromZero);
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, fmt, null, bytes, stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Fast 8-bit BGRA (display) bitmap from float planes already in [0,1].</summary>
    public static BitmapSource MakeDisplayImage(RGBImage rgb)
    {
        int w = rgb.Width, h = rgb.Height;
        var bytes = DisplayBytes(rgb);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>BGRA bytes (8-bit, values × 255 rounded, clamped) of an RGB image.</summary>
    public static byte[] DisplayBytes(RGBImage rgb)
    {
        int w = rgb.Width, h = rgb.Height, n = w * h;
        var bytes = new byte[n * 4];
        var r = rgb.R.Data; var g = rgb.G.Data; var b = rgb.B.Data;
        Par.Rows(h, w * 4, (y0, y1) =>
        {
            for (int i = y0 * w; i < y1 * w; i++)
            {
                int o = i * 4;
                bytes[o] = To8(b[i]); bytes[o + 1] = To8(g[i]); bytes[o + 2] = To8(r[i]); bytes[o + 3] = 255;
            }
        });
        return bytes;
    }

    static byte To8(float v)
    {
        float s = v * 255;
        if (!(s > 0)) return 0;
        if (s >= 255) return 255;
        return (byte)(s + 0.5f);
    }

    public static BitmapSource MakeDisplayImage(PlanarImage gray) => MakeDisplayImage(new RGBImage(gray, gray, gray));

    /// <summary>Small PNG (8-bit) in memory, e.g. for preset thumbnails.</summary>
    public static byte[] PngData(RGBImage rgb, int maxWidth = 160)
    {
        var img = rgb;
        while (img.Width > maxWidth * 2) img = img.Downsampled2x();
        if (img.Width > maxWidth)
        {
            int h = Math.Max(1, img.Height * maxWidth / img.Width);
            img = new RGBImage(img.R.Resized(maxWidth, h), img.G.Resized(maxWidth, h), img.B.Resized(maxWidth, h));
        }
        var bmp = MakeBitmap(img.Planes, 8);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource? BitmapFromPng(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data, false);
            var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var f = dec.Frames[0];
            f.Freeze();
            return f;
        }
        catch { return null; }
    }

    public static void Write(IReadOnlyList<PlanarImage> planes, string path, RasterFormat format, double jpegQuality = 0.92)
    {
        switch (format)
        {
            case RasterFormat.Fits32: FitsWriter.Write(planes, path, FitsWriter.Depth.Float32); return;
            case RasterFormat.Fits16: FitsWriter.Write(planes, path, FitsWriter.Depth.UInt16); return;
            case RasterFormat.Tiff16: TiffWriter.Write(planes, path, TiffWriter.SampleType.UInt16); return;
            case RasterFormat.Tiff32: TiffWriter.Write(planes, path, TiffWriter.SampleType.Float32); return;
        }
        BitmapEncoder enc;
        BitmapSource bmp;
        switch (format)
        {
            case RasterFormat.Png16: enc = new PngBitmapEncoder(); bmp = MakeBitmap(planes, 16); break;
            case RasterFormat.Png8: enc = new PngBitmapEncoder(); bmp = MakeBitmap(planes, 8); break;
            default: enc = new JpegBitmapEncoder { QualityLevel = (int)Math.Round(jpegQuality * 100) }; bmp = MakeBitmap(planes, 8); break;
        }
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) enc.Save(fs);
            File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is not ImageIOException)
        {
            try { File.Delete(tmp); } catch { }
            throw new ImageIOException(ImageIOErrorKind.CannotWrite, e.Message);
        }
    }
}
