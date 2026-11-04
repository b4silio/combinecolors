using System.IO;
using AstroCore;

namespace CombineColors.Model;

/// <summary>One source file to run StarNet2 on, with the channels it backs (three for a split colour file).</summary>
public sealed record StarRemovalItem(string Source, IReadOnlyList<ChannelKind> Kinds, string Starless, string Stars, bool AlreadyStarless, bool OutputsExist)
{
    public string Id => Source;
    public string KindsLabel => string.Join(", ", Kinds.Select(k => k.ShortName()));
}

public sealed record StarRemovalProgress(double Fraction, string Label, string Remaining);

/// <summary>
/// Finds, downloads and remembers the StarNet2 command-line program, and keeps track of the
/// user's acceptance of its license (StarNet2's terms require integrations to show the license
/// and get an explicit "I accept" before using it; downloads must come from the official site).
/// Every member runs on the UI thread; <see cref="Changed"/> fires on every phase change.
/// </summary>
public sealed class StarNetSetupModel
{
    public enum Phase { Checking, Missing, Downloading, Installing, NeedsLicense, Ready, Failed }

    readonly IUiDispatcher ui;
    public StarNetSetup Setup { get; }
    public Phase State { get; private set; } = Phase.Checking;
    public string? FailureMessage { get; private set; }
    public StarNetDownloadProgress Download { get; private set; }
    public StarNetInstallation? Installation { get; private set; }
    public StarNetTool? Tool => Installation?.Tool;
    public StarNetInfo? Info => Installation?.Info;
    /// <summary>The license text to accept (null: no license file next to the program — the website's applies).</summary>
    public string? LicenseText { get; private set; }
    /// <summary>Newest release on the official feed (after <see cref="CheckForUpdate"/>).</summary>
    public StarNetRelease? Latest { get; private set; }
    public event Action? Changed;
    CancellationTokenSource? downloadCancel;

    public StarNetSetupModel(IUiDispatcher ui, StarNetSetup? setup = null)
    {
        this.ui = ui;
        Setup = setup ?? new StarNetSetup();
    }

    public bool UpdateAvailable => Latest is StarNetRelease l && Info is StarNetInfo i && StarNetSetup.IsUpdateAvailable(i, l);
    public bool LicenseAccepted => Installation != null && Setup.IsLicenseAccepted(LicenseText);

    void Set(Phase p, string? failure = null) { State = p; FailureMessage = failure; Changed?.Invoke(); }

    /// <summary>Looks for StarNet2: the remembered path, the app's own install, then the standard places and the PATH.</summary>
    public void Refresh()
    {
        Set(Phase.Checking);
        Task.Run(() =>
        {
            StarNetInstallation? found = null;
            try { found = Setup.Locate(); } catch { }
            ui.Post(() => { if (found != null) Adopt(found); else { Installation = null; Set(Phase.Missing); } });
        });
    }

    /// <summary>Uses a file the user picked: the starnet2 program itself, the folder it is in, or the downloaded .zip archive.</summary>
    public void Choose(string path)
    {
        bool zip = Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
        Set(zip ? Phase.Installing : Phase.Checking);
        Task.Run(() =>
        {
            try
            {
                var inst = Setup.Choose(path);
                ui.Post(() => Adopt(inst));
            }
            catch (Exception e)
            {
                var msg = e.Message + (zip ? "" : $" Please pick the file called “{StarNet.ExecutableName}” from the StarNet2 command-line package.");
                ui.Post(() => Set(Phase.Failed, msg));
            }
        });
    }

    void Adopt(StarNetInstallation inst)
    {
        Installation = inst;
        Setup.Remember(inst);
        LicenseText = inst.ReadLicense();
        Set(LicenseAccepted ? Phase.Ready : Phase.NeedsLicense);
    }

    public void AcceptLicense()
    {
        Setup.AcceptLicense(LicenseText);
        if (Installation != null) Set(Phase.Ready);
    }

    /// <summary>Downloads the portable package for this platform from the official site, checks it and unpacks it.</summary>
    public void StartDownload()
    {
        downloadCancel?.Cancel();
        var cts = new CancellationTokenSource();
        downloadCancel = cts;
        Download = new StarNetDownloadProgress(0, 0);
        Set(Phase.Downloading);
        var progress = new Progress<StarNetDownloadProgress>(p => { if (State == Phase.Downloading && !cts.IsCancellationRequested) { Download = p; Changed?.Invoke(); } });
        Task.Run(async () =>
        {
            try
            {
                var release = await StarNetSetup.FetchLatestAsync(cts.Token) ?? StarNetRelease.Fallback();
                ui.Post(() => { Latest = release; Download = new StarNetDownloadProgress(0, release.SizeBytes ?? 0); Changed?.Invoke(); });
                var archive = Path.Combine(Path.GetTempPath(), $"starnet2-{Guid.NewGuid():N}.zip");
                try
                {
                    await StarNetSetup.DownloadAsync(release, archive, progress, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();
                    ui.Post(() => Set(Phase.Installing));
                    var inst = Setup.Install(archive, release.Sha256, release.Version);
                    ui.Post(() => { if (downloadCancel == cts) downloadCancel = null; Adopt(inst); });
                }
                finally { try { File.Delete(archive); } catch { } }
            }
            catch (OperationCanceledException) { ui.Post(Refresh); }
            catch (Exception e)
            {
                if (cts.IsCancellationRequested) { ui.Post(Refresh); return; }
                var msg = e is StarNetError ? e.Message
                    : $"The download did not work: {e.Message}. Check the internet connection and try again, or download StarNet2 from its website yourself.";
                ui.Post(() => Set(Phase.Failed, msg));
            }
        });
    }

    public void CancelDownload()
    {
        if (downloadCancel is CancellationTokenSource c) { c.Cancel(); downloadCancel = null; }
        else Refresh();
    }

    public void CheckForUpdate()
    {
        Task.Run(async () =>
        {
            var r = await StarNetSetup.FetchLatestAsync();
            if (r != null) ui.Post(() => { Latest = r; Changed?.Invoke(); });
        });
    }
}

public sealed partial class ProjectModel
{
    StarNetSetupModel? starNet;
    /// <summary>The StarNet2 program: found, downloaded, license accepted (remembered in starnet.json next to the preferences).</summary>
    public StarNetSetupModel StarNet { get => starNet ??= new StarNetSetupModel(ui); set => starNet = value; }

    StarRemovalProgress? starRemovalProgress;
    StarNetCancellation? starRemovalCancellation;
    /// <summary>Progress of the StarNet2 run (null when none).</summary>
    public StarRemovalProgress? StarRemovalProgress => starRemovalProgress;
    /// <summary>A StarNet2 run is in progress (tabs, close and the editors wait).</summary>
    public bool IsRemovingStars => starRemovalProgress != null;

    /// <summary>The loaded channels grouped by file, in channel order. Channels without a file (dual-band splits) are left out.</summary>
    public List<StarRemovalItem> StarRemovalItems(string? folder = null)
    {
        var kindsByPath = new Dictionary<string, List<ChannelKind>>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var k in ChannelKinds.All)
        {
            if (!sources.TryGetValue(k, out var s) || s.Path is not string p) continue;
            if (!kindsByPath.TryGetValue(p, out var list)) { kindsByPath[p] = list = new(); paths.Add(p); }
            list.Add(k);
        }
        return paths.Select(p =>
        {
            var (starless, stars) = AstroCore.StarRemoval.OutputPaths(p, folder);
            return new StarRemovalItem(p, kindsByPath[p], starless, stars, AstroCore.StarRemoval.IsStarless(p), File.Exists(starless) || File.Exists(stars));
        }).ToList();
    }

    /// <summary>Channels that cannot go through StarNet2 because no file backs them.</summary>
    public List<ChannelKind> ChannelsWithoutFile => ChannelKinds.All.Where(k => sources.TryGetValue(k, out var s) && s.Path == null).ToList();

    /// <summary>
    /// Runs StarNet2 on the files one after the other, writes <c>&lt;name&gt;_bg</c> and <c>&lt;name&gt;_stars</c> for
    /// each and then switches the channels to the _bg files (dust spots, gradient models, crop and
    /// painted corrections carry over) and puts the stars on the Stars tab. Stopping leaves the channels as they were.
    /// </summary>
    public void RemoveStars(IReadOnlyList<StarRemovalItem> items, StarRemovalOptions options, string? folder = null)
    {
        if (StarNet.Tool is not StarNetTool tool || !StarNet.LicenseAccepted || items.Count == 0 || IsRemovingStars) return;
        var cancel = new StarNetCancellation();
        starRemovalCancellation = cancel;
        starRemovalProgress = new StarRemovalProgress(0, "Starting StarNet2…", "");
        Notify(Change.Status); Notify(Change.Layer);
        var start = DateTime.UtcNow;
        Task.Run(() =>
        {
            var done = new List<(StarRemovalItem item, StarRemovalFileResult result)>();
            var errors = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                if (cancel.IsCancelled) break;
                var item = items[i];
                string label = $"{item.KindsLabel} — {Path.GetFileName(item.Source)}" + (items.Count > 1 ? $"  ({i + 1} of {items.Count})" : "");
                int index = i;
                ui.Post(() => UpdateStarRemoval((double)index / items.Count, label, start));
                try
                {
                    var r = AstroCore.StarRemoval.ProcessFile(item.Source, options, tool, folder,
                        p => ui.Post(() => UpdateStarRemoval((index + p) / items.Count, label, start)), cancel);
                    done.Add((item, r));
                }
                catch (StarNetError e) when (e.Kind == StarNetErrorKind.Cancelled) { break; }
                catch (Exception e) { errors.Add($"{Path.GetFileName(item.Source)}: {e.Message}"); }
            }
            bool stopped = cancel.IsCancelled;
            ui.Post(() => FinishStarRemoval(done, errors, stopped));
        });
    }

    public void StopStarRemoval() => starRemovalCancellation?.Cancel();

    void UpdateStarRemoval(double fraction, string label, DateTime start)
    {
        if (starRemovalProgress == null) return;
        double elapsed = (DateTime.UtcNow - start).TotalSeconds;
        string remaining = "";
        if (fraction > 0.03)
        {
            double left = elapsed / fraction * (1 - fraction);
            remaining = left < 60 ? "less than a minute left" : $"about {(int)Math.Round(left / 60)} minute{(left >= 90 ? "s" : "")} left";
        }
        starRemovalProgress = new StarRemovalProgress(Math.Max(fraction, starRemovalProgress.Fraction), label, remaining);
        Notify(Change.Status);
    }

    void FinishStarRemoval(List<(StarRemovalItem item, StarRemovalFileResult result)> done, List<string> errors, bool stopped)
    {
        starRemovalProgress = null;
        starRemovalCancellation = null;
        Notify(Change.Status); Notify(Change.Layer);
        if (stopped) { StatusText = "Star removal stopped — the channels were not changed."; return; }
        if (errors.Count > 0) ErrorMessage = "StarNet2 could not process:\n" + string.Join("\n", errors);
        if (done.Count == 0) return;
        // Painted corrections live next to each image: give the new files a copy.
        foreach (var (item, r) in done)
            foreach (var k in item.Kinds)
                if (correctionMaps.TryGetValue(k, out var m)) { try { m.Unoriented(Orientation).Write(r.Starless, k); } catch { } }
        var entries = done.SelectMany(d => d.item.Kinds.Select(k => (k, d.result.Starless))).ToList();
        var starEntries = done.SelectMany(d => d.item.Kinds.Select(k => (k, d.result.Stars))).ToList();
        bool stretchedStars = done.All(d => d.result.Processing == StarRemovalProcessing.AlreadyStretched);
        int count = entries.Count;
        string stretchedNote = done.Any(d => d.result.Processing == StarRemovalProcessing.AlreadyStretched) ? " (already-stretched data processed as they are)" : "";
        IsLoading = true;
        LoadingMessage = "Loading the starless channels…";
        Task.Run(() =>
        {
            var (items, errs) = LoadEntries(entries);
            var (starItems, starErrs) = LoadEntries(starEntries);
            ui.Post(() =>
            {
                var all = errs.Concat(starErrs).ToList();
                if (all.Count > 0) ErrorMessage = string.Join("\n", all);
                statusHint = $"stars removed from {count} channel{(count == 1 ? "" : "s")} with StarNet2{stretchedNote}; the stars are on the Stars tab";
                if (items.Count == 0) { IsLoading = false; return; }
                // The starless files replace the Background channels, the stars fill the Stars tab
                // (not shown) and the combination is switched on.
                LoadIntoLayer(ImageLayer.Background, items, completion: () =>
                {
                    LoadIntoLayer(ImageLayer.Stars, starItems, stretchedStars, completion: () =>
                    {
                        if (ActiveLayer != ImageLayer.Background) return;
                        UpdateSettings(s => { s.Stars.UseMask = false; s.Stars.Enabled = true; });
                        UpdateStarsComposite();
                    });
                });
            });
        });
    }
}
