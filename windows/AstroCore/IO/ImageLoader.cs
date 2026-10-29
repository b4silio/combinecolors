namespace AstroCore;

/// <summary>A file loaded from disk, before it is assigned to channel slots.</summary>
public sealed class LoadedFile
{
    public required string Path { get; init; }
    public required IReadOnlyList<PlanarImage> Planes { get; init; }
    public FitsHeader? Header { get; init; }
    public string? FilterName { get; init; }
    public ChannelKind? DetectedKind { get; init; }
    public required string FormatDescription { get; init; }

    public int Width => Planes[0].Width;
    public int Height => Planes[0].Height;
    public bool IsColor => Planes.Count >= 3;
    public string FileName => System.IO.Path.GetFileName(Path);
}

public static class ImageLoader
{
    public static readonly HashSet<string> FitsExtensions = new(StringComparer.OrdinalIgnoreCase) { "fits", "fit", "fts" };
    public static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase) { "tif", "tiff", "png", "jpg", "jpeg" };

    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static bool IsSupported(string path)
    {
        var ext = Extension(path);
        return FitsExtensions.Contains(ext) || RasterExtensions.Contains(ext);
    }

    public static LoadedFile Load(string path)
    {
        var ext = Extension(path);
        string fileName = Path.GetFileName(path);
        if (FitsExtensions.Contains(ext))
        {
            var f = FitsReader.Read(path);
            var filter = f.Header.String("FILTER") ?? f.Header.String("FILT-1");
            var kind = f.Planes.Count == 1 ? ChannelDetection.Detect(fileName, filter) : null;
            string depth = f.Bitpix switch
            {
                -32 => "32-bit float",
                -64 => "64-bit float",
                16 => "16-bit integer",
                32 => "32-bit integer",
                8 => "8-bit",
                _ => $"BITPIX {f.Bitpix}",
            };
            return new LoadedFile
            {
                Path = path, Planes = f.Planes, Header = f.Header, FilterName = filter, DetectedKind = kind,
                FormatDescription = $"FITS {depth}{(f.Planes.Count > 1 ? $" ×{f.Planes.Count}" : "")}",
            };
        }
        var r = RasterReader.Read(path);
        var k = r.Planes.Count == 1 ? ChannelDetection.DetectFileName(fileName) : null;
        var d = r.IsFloat ? "32-bit float" : $"{r.BitsPerComponent}-bit";
        return new LoadedFile
        {
            Path = path, Planes = r.Planes, Header = null, FilterName = null, DetectedKind = k,
            FormatDescription = $"{ext.ToUpperInvariant()} {d}{(r.Planes.Count > 1 ? " RGB" : "")}",
        };
    }
}
