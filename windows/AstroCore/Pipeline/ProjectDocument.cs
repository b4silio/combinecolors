namespace AstroCore;

/// <summary>A named snapshot of the combination settings with a small preview image.</summary>
public sealed partial class SettingsPreset : IEquatable<SettingsPreset>
{
    public Guid Id = Guid.NewGuid();
    public string Name;
    public CombineSettings Settings;
    public byte[]? ThumbnailPng;
    public DateTime Date = DateTime.UtcNow;

    public SettingsPreset(string name, CombineSettings settings, byte[]? thumbnailPng = null)
    {
        Name = name; Settings = settings; ThumbnailPng = thumbnailPng;
    }

    public SettingsPreset Clone() => new(Name, Settings.Clone(), ThumbnailPng == null ? null : (byte[])ThumbnailPng.Clone()) { Id = Id, Date = Date };

    public bool Equals(SettingsPreset? o) => o != null && Id == o.Id && Name == o.Name && Settings.Equals(o.Settings) && Date == o.Date
        && ((ThumbnailPng == null && o.ThumbnailPng == null) || (ThumbnailPng != null && o.ThumbnailPng != null && ThumbnailPng.AsSpan().SequenceEqual(o.ThumbnailPng)));
    public override bool Equals(object? obj) => Equals(obj as SettingsPreset);
    public override int GetHashCode() => HashCode.Combine(Id, Name);
}

/// <summary>One set of channels with its own settings (the Stars tab of a project).</summary>
public sealed partial class LayerDocument
{
    public List<ProjectDocument.ChannelRef> Channels = new();
    public CombineSettings Settings = new();
    public DustSettings Dust = new();
    public GradientSettings Gradient = new();
    public CorrectionSettings Corrections = new();
    public List<SettingsPreset> Presets = new();
}

/// <summary>Saved project: channel file paths + settings.</summary>
public sealed partial class ProjectDocument
{
    public sealed class ChannelRef
    {
        public ChannelKind Kind;
        public string Path;
        public byte[]? Bookmark;
        public ChannelRef(ChannelKind kind, string path, byte[]? bookmark = null) { Kind = kind; Path = path; Bookmark = bookmark; }
    }

    public int Version = 1;
    public List<ChannelRef> Channels = new();
    public CombineSettings Settings = new();
    public string? StarsFile = null;
    /// <summary>Shared crop applied to every channel (statistics, thumbnails, export).</summary>
    public PixelRect? Crop = null;
    /// <summary>Saved presets (settings + thumbnail) for quick switching.</summary>
    public List<SettingsPreset> Presets = new();
    /// <summary>Quarter turns clockwise applied to every channel at load (0…3); the crop is in oriented coordinates.</summary>
    public int Rotation = 0;
    /// <summary>Horizontal mirror applied before the rotation (see <see cref="AstroCore.Orientation"/>).</summary>
    public bool Flipped = false;
    /// <summary>Fine rotation in degrees after the quarter turns (see <see cref="AstroCore.Orientation"/>); the crop is in the turned picture.</summary>
    public double Angle = 0;
    /// <summary>Dust spots healed on the loaded channels (oriented coordinates) and the estimation parameters.</summary>
    public DustSettings Dust = new();
    /// <summary>Gradient models removed from the loaded channels (oriented coordinates) and the shared protected regions.</summary>
    public GradientSettings Gradient = new();
    /// <summary>Strength of the hand-painted corrections per channel and the brush defaults (the maps are sidecar files).</summary>
    public CorrectionSettings Corrections = new();
    /// <summary>
    /// The Stars tab: star channels processed on their own and combined with the background
    /// (the fields above) by the "Combine stars" block. Crop and orientation are shared.
    /// </summary>
    public LayerDocument? StarsLayer = null;

    public Orientation Orientation
    {
        get => new(Rotation, Flipped, Angle);
        set { Rotation = value.Rotation; Flipped = value.Flipped; Angle = value.Angle; }
    }

    /// <summary>Pretty-printed JSON with sorted keys (the same layout the macOS build writes).</summary>
    public string Json() => SettingsJson.Serialize(ToJson(), pretty: true);

    public byte[] Data() => System.Text.Encoding.UTF8.GetBytes(Json());

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Data());
        File.Move(tmp, path, true);
    }

    public static ProjectDocument Load(string path) => FromJson(SettingsJson.ParseObject(File.ReadAllText(path)));

    public static ProjectDocument Parse(string json) => FromJson(SettingsJson.ParseObject(json));
}
