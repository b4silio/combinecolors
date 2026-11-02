using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AstroCore;

namespace CombineColors.Model;

/// <summary>Display bitmaps for Avalonia from the renderer's float planes (the role WIC plays in the WPF build).</summary>
public static class DisplayBitmaps
{
    /// <summary>8-bit BGRA bitmap of an RGB image already in [0,1]. Safe to call from a background thread.</summary>
    public static Bitmap Make(RGBImage rgb) => FromBgra(RasterWriter.DisplayBytes(rgb), rgb.Width, rgb.Height);

    public static Bitmap Make(PlanarImage gray) => Make(new RGBImage(gray, gray, gray));

    static Bitmap FromBgra(byte[] bytes, int w, int h)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using (var fb = wb.Lock())
        {
            int row = w * 4;
            if (fb.RowBytes == row) Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
            else for (int y = 0; y < h; y++) Marshal.Copy(bytes, y * row, fb.Address + y * fb.RowBytes, row);
        }
        return wb;
    }

    /// <summary>Decodes an in-memory PNG (preset thumbnails); null when it cannot be read.</summary>
    public static Bitmap? FromPng(byte[] data)
    {
        try { using var ms = new MemoryStream(data, false); return new Bitmap(ms); }
        catch { return null; }
    }

    /// <summary>Region of a rendered frame as a bitmap (pixel coordinates of the frame).</summary>
    public static Bitmap Crop(RGBImage rgb, int x, int y, int w, int h) => Make(new RGBImage(rgb.R.Cropped(x, y, w, h), rgb.G.Cropped(x, y, w, h), rgb.B.Cropped(x, y, w, h)));
}

public static class RectExtensions
{
    /// <summary>True for a rectangle without area (Avalonia has no Rect.Empty).</summary>
    public static bool IsEmptyRect(this Rect r) => r.Width <= 0 || r.Height <= 0;
}
