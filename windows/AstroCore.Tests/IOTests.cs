using System.Buffers.Binary;
using System.Text;
using AstroCore;

namespace AstroCore.Tests;

public class FitsTests
{
    [Fact]
    public void HeaderCardParsing()
    {
        var c = FitsHeader.ParseCard("FILTER  = 'Hydrogen/alpha'     / filter used                                   ")!.Value;
        Assert.Equal("FILTER", c.Key);
        var h = new FitsHeader(); h.Append(c);
        Assert.Equal("Hydrogen/alpha", h.String("FILTER"));
        Assert.Equal("filter used", c.Comment);
        var n = FitsHeader.ParseCard("NAXIS1  =                 7272 / n'th data dimension                            ")!.Value;
        h.Append(n);
        Assert.Equal(7272, h.Int("NAXIS1"));
        var d = FitsHeader.ParseCard("BZERO   =                  0.E0 / no offset                                     ")!.Value;
        h.Append(d);
        Assert.Equal(0, h.Double("BZERO"));
        var q = FitsHeader.ParseCard("NAME    = 'it''s'              / quoted                                        ")!.Value;
        h.Append(q);
        Assert.Equal("it's", h.String("NAME"));
    }

    [Fact]
    public void Float32RoundTrip()
    {
        var img = Synth.Channel(37, 11);
        var path = Synth.TempPath("rt32.fits");
        FitsWriter.Write([img], path, FitsWriter.Depth.Float32, [("FILTER", "Ha", "filter")]);
        var back = FitsReader.Read(path);
        Assert.Equal(37, back.Width); Assert.Equal(11, back.Height); Assert.Equal(-32, back.Bitpix);
        Assert.Equal(0, back.Planes[0].MaxAbsDifference(img));
        Assert.Equal("Ha", back.Header.String("FILTER"));
        Assert.Equal("TOP-DOWN", back.Header.String("ROWORDER"));
        Assert.Equal(37, FitsReader.ReadHeader(path).Int("NAXIS1"));
    }

    [Fact]
    public void UInt16RoundTripAndRgb()
    {
        var r = new PlanarImage(5, 3, 0.25f); var g = new PlanarImage(5, 3, 0.5f); var b = new PlanarImage(5, 3, 1.0f);
        var path = Synth.TempPath("rt16.fits");
        FitsWriter.Write([r, g, b], path, FitsWriter.Depth.UInt16);
        var back = FitsReader.Read(path);
        Assert.Equal(3, back.Planes.Count);
        Assert.Equal(3, back.Header.Int("NAXIS3"));
        A.Close(0.25f, back.Planes[0][0, 0], 1f / 65535);
        A.Close(0.5f, back.Planes[1][2, 2], 1f / 65535);
        A.Close(1.0f, back.Planes[2][4, 2], 1f / 65535);
    }

    [Fact]
    public void SyntheticInt16WithoutBzeroAndBottomUp()
    {
        // Build a minimal signed 16-bit FITS by hand with ROWORDER = BOTTOM-UP.
        var header = new StringBuilder();
        void Card(string s) => header.Append(s.PadRight(80));
        Card("SIMPLE  =                    T");
        Card("BITPIX  =                   16");
        Card("NAXIS   =                    2");
        Card("NAXIS1  =                    2");
        Card("NAXIS2  =                    2");
        Card("ROWORDER= 'BOTTOM-UP'");
        Card("END");
        while (header.Length % 2880 != 0) header.Append(' ');
        var data = new List<byte>(Encoding.ASCII.GetBytes(header.ToString()));
        // rows stored bottom-up: first stored row is the bottom of the image.
        short[] values = [-32768, 0, 32767, -16384];
        foreach (var v in values) { var be = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(be, v); data.AddRange(be); }
        data.AddRange(new byte[2880 - 8]);
        var img = FitsReader.Read(data.ToArray());
        // After flip the top row is the second stored row.
        A.Close(1.0f, img.Planes[0][0, 0], 1e-4f);        // 32767 + 32768 → 65535
        A.Close(0.25f, img.Planes[0][1, 0], 1e-4f);       // -16384 + 32768 → 16384
        A.Close(0.0f, img.Planes[0][0, 1], 1e-4f);
        A.Close(0.5f, img.Planes[0][1, 1], 1e-4f);
    }

    [Fact]
    public void RejectsNonFits()
    {
        Assert.Throws<FitsException>(() => FitsReader.Read(new byte[5000]));
    }
}

public class RasterTests
{
    [Fact]
    public void Tiff16GrayRoundTrip()
    {
        var img = new PlanarImage(16, 8);
        for (int i = 0; i < img.Count; i++) img.Data[i] = i / (float)(img.Count - 1);
        var path = Synth.TempPath("gray16.tif");
        RasterWriter.Write([img], path, RasterFormat.Tiff16);
        var back = RasterReader.Read(path);
        Assert.Single(back.Planes);
        Assert.Equal(16, back.BitsPerComponent);
        Assert.True(back.Planes[0].MaxAbsDifference(img) < 1.0f / 65535);
        var loaded = ImageLoader.Load(path);
        Assert.Equal("TIF 16-bit", loaded.FormatDescription);
        // The Windows Imaging Component reads our TIFF the same way.
        var wic = RasterReader.ReadWithWic(File.ReadAllBytes(path));
        Assert.True(wic.Planes[0].MaxAbsDifference(img) < 1.0f / 65535);
    }

    [Fact]
    public void Tiff16RgbRoundTrip()
    {
        var r = new PlanarImage(9, 7, 0.1f); var g = new PlanarImage(9, 7, 0.6f); var b = new PlanarImage(9, 7, 0.9f);
        var path = Synth.TempPath("rgb16.tif");
        RasterWriter.Write([r, g, b], path, RasterFormat.Tiff16);
        var back = RasterReader.Read(path);
        Assert.Equal(3, back.Planes.Count);
        A.Close(0.1f, back.Planes[0][4, 3], 1e-4f);
        A.Close(0.6f, back.Planes[1][4, 3], 1e-4f);
        A.Close(0.9f, back.Planes[2][4, 3], 1e-4f);
        var wic = RasterReader.ReadWithWic(File.ReadAllBytes(path));
        A.Close(0.6f, wic.Planes[1][4, 3], 1e-4f);
    }

    [Fact]
    public void Tiff32FloatRoundTrip()
    {
        var img = Synth.Channel(20, 10);
        var path = Synth.TempPath("f32.tif");
        RasterWriter.Write([img], path, RasterFormat.Tiff32);
        var back = RasterReader.Read(path);
        Assert.True(back.IsFloat);
        Assert.True(back.Planes[0].MaxAbsDifference(img) < 1e-6f);
    }

    [Fact]
    public void TiffReaderHandlesBigEndianPredictorAndStrips()
    {
        // Big-endian 16-bit gray, two strips, horizontal predictor: written by hand.
        int w = 6, h = 4;
        var values = new ushort[w * h];
        for (int i = 0; i < values.Length; i++) values[i] = (ushort)(1000 + 37 * i);
        var pixelBytes = new byte[w * h * 2];
        for (int y = 0; y < h; y++)
        {
            ushort prev = 0;
            for (int x = 0; x < w; x++)
            {
                ushort v = values[y * w + x];
                ushort diff = x == 0 ? v : (ushort)(v - prev);
                BinaryPrimitives.WriteUInt16BigEndian(pixelBytes.AsSpan((y * w + x) * 2), diff);
                prev = v;
            }
        }
        var ms = new MemoryStream();
        void U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        ms.Write("MM"u8); U16(42); U32(8);
        int stripBytes = w * 2 * 2;   // 2 rows per strip
        uint ifdOffset = 8;
        (int tag, int type, uint count, uint value)[] tags =
        [
            (256, 3, 1, (uint)w << 16), (257, 3, 1, (uint)h << 16), (258, 3, 1, 16u << 16), (259, 3, 1, 1u << 16), (262, 3, 1, 1u << 16),
            (273, 4, 2, 0), (277, 3, 1, 1u << 16), (278, 3, 1, 2u << 16), (279, 4, 2, 0), (317, 3, 1, 2u << 16),
        ];
        uint afterIfd = ifdOffset + 2 + (uint)tags.Length * 12 + 4;
        uint offsetsArr = afterIfd, countsArr = afterIfd + 8, dataStart = afterIfd + 16;
        U16(tags.Length);
        foreach (var (tag, type, count, value) in tags)
        {
            U16(tag); U16(type); U32(count);
            if (tag == 273) U32(offsetsArr); else if (tag == 279) U32(countsArr); else U32(value);
        }
        U32(0);
        U32(dataStart); U32(dataStart + (uint)stripBytes);
        U32((uint)stripBytes); U32((uint)stripBytes);
        ms.Write(pixelBytes);
        var res = TiffReader.Read(ms.ToArray());
        Assert.Equal(16, res.BitsPerSample);
        for (int i = 0; i < values.Length; i++) A.Close(values[i] / 65535f, res.Planes[0].Data[i], 1e-6f, $"px {i}");
    }

    [Fact]
    public void Png16AndJpeg()
    {
        var img = new PlanarImage(8, 8, 0.5f);
        var path = Synth.TempPath("x.png");
        RasterWriter.Write([img, img, img], path, RasterFormat.Png16);
        var back = RasterReader.Read(path);
        A.Close(0.5f, back.Planes[0][1, 1], 1e-3f);
        Assert.Equal(16, back.BitsPerComponent);
        var jpath = Synth.TempPath("x.jpg");
        RasterWriter.Write([img, img, img], jpath, RasterFormat.Jpeg);
        Assert.True(File.Exists(jpath));
        var jb = RasterReader.Read(jpath);
        Assert.Equal(3, jb.Planes.Count);
        A.Close(0.5f, jb.Planes[1][4, 4], 0.02f);
    }

    [Fact]
    public void DisplayImage()
    {
        var rgb = new RGBImage(4, 4);
        rgb.R.Data[0] = 1; rgb.G.Data[0] = 0.5f; rgb.B.Data[0] = 2;  // clamped
        var bytes = RasterWriter.DisplayBytes(rgb);
        Assert.Equal(255, bytes[2]); Assert.Equal(128, bytes[1]); Assert.Equal(255, bytes[0]); Assert.Equal(255, bytes[3]);
        var png = RasterWriter.PngData(rgb);
        Assert.True(png.Length > 20);
        Assert.NotNull(RasterWriter.BitmapFromPng(png));
    }
}

public class ChannelDetectionTests
{
    [Fact]
    public void FilterKeyword()
    {
        Assert.Equal(ChannelKind.Ha, ChannelDetection.DetectFilterName("Hydrogen-alpha"));
        Assert.Equal(ChannelKind.OIII, ChannelDetection.DetectFilterName("Oxygen III"));
        Assert.Equal(ChannelKind.SII, ChannelDetection.DetectFilterName("Sulphur II"));
        Assert.Equal(ChannelKind.Red, ChannelDetection.DetectFilterName("Red"));
        Assert.Equal(ChannelKind.Luminance, ChannelDetection.DetectFilterName("L"));
        Assert.Equal(ChannelKind.Hb, ChannelDetection.DetectFilterName("Hb"));
        Assert.Null(ChannelDetection.DetectFilterName("Unknown-xyz"));
    }

    [Fact]
    public void FileNames()
    {
        Assert.Equal(ChannelKind.OIII, ChannelDetection.DetectFileName("Soul_Core-Oxygen_III-session_1-lpc.fits"));
        Assert.Equal(ChannelKind.Ha, ChannelDetection.DetectFileName("Soul_Core-Hydrogen-alpha-session_1-lpc.fits"));
        Assert.Equal(ChannelKind.SII, ChannelDetection.DetectFileName("Soul_Core-Sulphur_II-session_1-lpc.fits"));
        Assert.Equal(ChannelKind.Ha, ChannelDetection.DetectFileName("LowersNebula-36h-Ha.tif"));
        Assert.Equal(ChannelKind.OIII, ChannelDetection.DetectFileName("LowersNebula-36h-O3.tif"));
        Assert.Equal(ChannelKind.SII, ChannelDetection.DetectFileName("LowersNebula-36h-S2.tif"));
        Assert.Equal(ChannelKind.Red, ChannelDetection.DetectFileName("M31_R.fit"));
        Assert.Equal(ChannelKind.Luminance, ChannelDetection.DetectFileName("master_L.fits"));
        Assert.Equal(ChannelKind.SII, ChannelDetection.DetectFileName("ngc7000_SII_300s.fits"));
        Assert.Equal(ChannelKind.Blue, ChannelDetection.DetectFileName("Blue_stack.tif"));
        Assert.Null(ChannelDetection.DetectFileName("stars.tif"));
        Assert.Equal(ChannelKind.Green, ChannelDetection.Detect("stars.tif", "Green"));
        Assert.Equal(ChannelKind.Ha, ChannelDetection.DetectFileName(@"C:\data\Soul_25h-top60-Hydrogen-alpha-0228_0301.fits"));
    }
}
