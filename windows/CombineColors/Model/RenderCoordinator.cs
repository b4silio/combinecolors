using System.Windows;
using System.Windows.Media.Imaging;
using AstroCore;

namespace CombineColors.Model;

/// <summary>A rendered preview frame: a region of a pyramid level, ready to display.</summary>
public sealed class PreviewFrame
{
    public required BitmapSource Bitmap { get; init; }
    public required RGBImage Rgb { get; init; }
    /// <summary>Pyramid level used.</summary>
    public int Level { get; init; }
    /// <summary>Region covered by the frame, in full-resolution image pixels.</summary>
    public Rect Rect { get; init; }
    public double Millis { get; init; }
    public List<ResolvedStretch>? PostStretch { get; init; }
    public required Dictionary<ChannelKind, ResolvedStretch> ChannelStretch { get; init; }
    public required CombineSettings Settings { get; init; }
}

public sealed class PreviewRequest
{
    public required Dictionary<ChannelKind, ChannelSource> Sources;
    public required CombineSettings Settings;
    public required RenderContext Context;
    public required Dictionary<ChannelKind, PlanarImage> AnalysisSamples;
    public List<ResolvedStretch>? FixedPostStretch;
    /// <summary>Visible region in full-resolution pixels (null = whole image).</summary>
    public Rect? VisibleRect;
    /// <summary>Device pixels per full-resolution image pixel.</summary>
    public double Scale;
    public int PixelBudget;
    public int Generation;
}

/// <summary>Serial, latest-wins background renderer.</summary>
public sealed class RenderCoordinator
{
    readonly object gate = new();
    PreviewRequest? pending;
    bool busy;
    public Action<PreviewFrame, int>? OnFrame;
    public Action<Exception, int>? OnError;

    public void Submit(PreviewRequest req)
    {
        bool start;
        lock (gate)
        {
            pending = req;
            start = !busy;
            if (start) busy = true;
        }
        if (start) Task.Run(Drain);
    }

    void Drain()
    {
        while (true)
        {
            PreviewRequest req;
            lock (gate)
            {
                if (pending == null) { busy = false; return; }
                req = pending; pending = null;
            }
            try
            {
                var frame = Render(req);
                OnFrame?.Invoke(frame, req.Generation);
            }
            catch (Exception e) { OnError?.Invoke(e, req.Generation); }
        }
    }

    /// <summary>Chooses the pyramid level and crop for a viewport, renders it and converts it for display.</summary>
    public static PreviewFrame Render(PreviewRequest req)
    {
        var any = req.Sources.Values.FirstOrDefault() ?? throw RenderException.NoOutput();
        var pyramid = any.Pyramid;
        int fullW = any.Width, fullH = any.Height;
        var full = new Rect(0, 0, fullW, fullH);
        var visible = req.VisibleRect is Rect vr ? Rect.Intersect(vr, full) : full;
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) visible = full;
        // Expand by a margin so small pans do not trigger new renders.
        double margin = Math.Max(visible.Width, visible.Height) * 0.35;
        var region = Rect.Intersect(new Rect(visible.X - margin, visible.Y - margin, visible.Width + 2 * margin, visible.Height + 2 * margin), full);
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0) region = full;
        int level = pyramid.LevelIndexForScale(req.Scale);
        long Pixels(int l) { double s = Math.Pow(2.0, -l); return (long)(region.Width * s) * (long)(region.Height * s); }
        while (level + 1 < pyramid.Count && Pixels(level) > req.PixelBudget) level++;
        double sc = Math.Pow(2.0, -level);
        int lw = pyramid.Level(level).Width, lh = pyramid.Level(level).Height;
        int x0 = Math.Max(0, (int)Math.Floor(region.Left * sc)), y0 = Math.Max(0, (int)Math.Floor(region.Top * sc));
        int x1 = Math.Min(lw, (int)Math.Ceiling(region.Right * sc)), y1 = Math.Min(lh, (int)Math.Ceiling(region.Bottom * sc));
        int cw = Math.Max(1, x1 - x0), ch = Math.Max(1, y1 - y0);
        bool wholeLevel = x0 == 0 && y0 == 0 && cw == lw && ch == lh;
        var planes = new Dictionary<ChannelKind, PlanarImage>();
        foreach (var (k, src) in req.Sources)
        {
            var lvl = src.Pyramid.Level(level);
            planes[k] = wholeLevel ? lvl : lvl.Cropped(x0, y0, cw, ch);
        }
        var result = Renderer.Render(new RenderRequest(planes, req.Settings, req.Context, req.AnalysisSamples, req.FixedPostStretch, scale: (float)sc));
        var bmp = RasterWriter.MakeDisplayImage(result.Image);
        double inv = 1 / sc;
        var rect = new Rect(x0 * inv, y0 * inv, cw * inv, ch * inv);
        return new PreviewFrame
        {
            Bitmap = bmp, Rgb = result.Image, Level = level, Rect = rect, Millis = result.Millis, PostStretch = result.PostStretch,
            ChannelStretch = result.ChannelStretch, Settings = req.Settings,
        };
    }
}

/// <summary>Renders palette thumbnails in the background at low priority, one batch at a time.</summary>
public sealed class ThumbnailCoordinator
{
    int generation;
    readonly object gate = new();
    public Action<string, BitmapSource, int>? OnThumbnail;

    public sealed class Job
    {
        public required List<Palette> Palettes;
        public required Dictionary<ChannelKind, ChannelSource> Sources;
        public required CombineSettings Settings;
        public required RenderContext Context;
        public required Dictionary<ChannelKind, PlanarImage> AnalysisSamples;
        /// <summary>Region in full-resolution pixels (null = whole image).</summary>
        public Rect? Region;
        public int MaxPixels;
    }

    public int Submit(Job job)
    {
        int gen;
        lock (gate) gen = ++generation;
        var thread = new Thread(() => Run(job, gen)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "thumbnails" };
        thread.Start();
        return gen;
    }

    void Run(Job job, int gen)
    {
        var any = job.Sources.Values.FirstOrDefault();
        if (any == null) return;
        var full = new Rect(0, 0, any.Width, any.Height);
        var region = job.Region is Rect r ? Rect.Intersect(r, full) : full;
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0) region = full;
        // Choose the level so that the region fits the budget.
        int level = 0;
        while (level + 1 < any.Pyramid.Count)
        {
            double s = Math.Pow(2.0, -level);
            if ((long)(region.Width * s) * (long)(region.Height * s) <= job.MaxPixels) break;
            level++;
        }
        double sc = Math.Pow(2.0, -level);
        int x0 = (int)(region.X * sc), y0 = (int)(region.Y * sc);
        int cw = Math.Max(1, (int)(region.Width * sc)), ch = Math.Max(1, (int)(region.Height * sc));
        var planes = new Dictionary<ChannelKind, PlanarImage>();
        foreach (var (k, src) in job.Sources)
        {
            var lvl = src.Pyramid.Level(level);
            planes[k] = (x0 == 0 && y0 == 0 && cw == lvl.Width && ch == lvl.Height) ? lvl : lvl.Cropped(x0, y0, cw, ch);
        }
        foreach (var palette in job.Palettes)
        {
            bool stale;
            lock (gate) stale = gen != generation;
            if (stale) return;
            var settings = job.Settings.Clone();
            settings.Select(palette);
            try
            {
                var res = Renderer.Render(new RenderRequest(planes, settings, job.Context, job.AnalysisSamples, scale: (float)sc));
                var bmp = RasterWriter.MakeDisplayImage(res.Image);
                OnThumbnail?.Invoke(palette.Id, bmp, gen);
            }
            catch { /* a palette that cannot render just shows no thumbnail */ }
        }
    }
}
