using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AstroCore;
using CombineColors.Model;
using Orientation = System.Windows.Controls.Orientation;

namespace CombineColors.Views;

/// <summary>
/// Star removal with StarNet2: guides the one-time setup (download or locate the program, accept
/// its license) and then runs it on the loaded channels. Written for people who rarely install
/// programs: one big button per step, plain words, no command prompt.
/// </summary>
public sealed class StarRemovalWindow
{
    readonly Window w;
    readonly ProjectModel model;
    readonly StarNetSetupModel setup;
    readonly StackPanel content = new();
    readonly HashSet<string> selected;
    string? folder;
    bool accepted, showManual, showOptions;
    StarRemovalMethod method;
    bool wasRemoving;

    public static void Show(Window owner, ProjectModel model) => new StarRemovalWindow(owner, model).w.ShowDialog();

    StarRemovalWindow(Window owner, ProjectModel model)
    {
        this.model = model;
        setup = model.StarNet;
        method = EnumRaw.Parse<StarRemovalMethod>(model.Preferences.GetString("starRemovalMethod")) ?? StarRemovalMethod.StarnetLinear;
        selected = model.StarRemovalItems().Where(i => !i.AlreadyStarless).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        w = DialogBase.Make(owner, "Remove Stars", 660);
        var root = new StackPanel { Margin = new Thickness(22) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "☆", FontSize = 30, Foreground = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Remove Stars", FontSize = 20, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock { Text = "with StarNet2 by Mikita Misiura", FontSize = 13, Foreground = Brushes.Gray });
        head.Children.Add(titles);
        root.Children.Add(head);
        root.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(content);
        w.Content = root;
        w.FontSize = 13.5;
        setup.Changed += Refresh;
        model.Changed += OnModelChanged;
        w.Closing += (_, e) => { if (model.IsRemovingStars) e.Cancel = true; };
        w.Closed += (_, _) => { setup.Changed -= Refresh; model.Changed -= OnModelChanged; };
        if (setup.State == StarNetSetupModel.Phase.Checking || setup.Tool == null) setup.Refresh(); else setup.CheckForUpdate();
        Refresh();
    }

    void OnModelChanged(string what)
    {
        if (what != Change.Status && what != Change.Layer) return;
        // The channels switch to the new files when the run ends: back to the main window.
        if (wasRemoving && !model.IsRemovingStars) { wasRemoving = false; w.Dispatcher.BeginInvoke(() => w.Close()); return; }
        wasRemoving = model.IsRemovingStars;
        Refresh();
    }

    void Refresh()
    {
        content.Children.Clear();
        if (model.IsRemovingStars && model.StarRemovalProgress is StarRemovalProgress p) { Running(p); return; }
        switch (setup.State)
        {
            case StarNetSetupModel.Phase.Checking: Busy("Looking for StarNet2 on this computer…"); Footer(Close()); break;
            case StarNetSetupModel.Phase.Missing: Missing(); break;
            case StarNetSetupModel.Phase.Downloading: Downloading(); break;
            case StarNetSetupModel.Phase.Installing: Busy("Unpacking StarNet2…"); break;
            case StarNetSetupModel.Phase.NeedsLicense: License(); break;
            case StarNetSetupModel.Phase.Ready: Ready(); break;
            case StarNetSetupModel.Phase.Failed: Failed(setup.FailureMessage ?? "Something went wrong."); break;
        }
    }

    // ---- Setup steps -------------------------------------------------------------------------------

    void Missing()
    {
        Text("Star removal is done by StarNet2, a free program made by Mikita Misiura. It is not part of CombineColors: it has to be downloaded once from its official website, starnetastro.com.");
        Text($"CombineColors can do this for you. It takes a minute or two (about {SizeLabel} to download) and needs no administrator rights. Afterwards you will be asked to read and accept the StarNet2 license.");
        // Not the default button: a stray Return must not start a 190 MB download.
        var dl = new Button { Content = "⬇  Download and Install StarNet2", FontSize = 14, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 6, 0, 10), Background = (Brush)Application.Current.Resources["AccentBrush"], Foreground = Brushes.White };
        dl.Click += (_, _) => setup.StartDownload();
        content.Children.Add(dl);
        content.Children.Add(Row(Btn("I already have StarNet2…", Locate), Btn("Open the StarNet2 Website", OpenWebsite)));
        Manual("I prefer to install it myself");
        Footer(Close());
    }

    void Manual(string title)
    {
        var exp = new Expander { Header = title, IsExpanded = showManual, Margin = new Thickness(0, 10, 0, 0) };
        exp.Expanded += (_, _) => showManual = true; exp.Collapsed += (_, _) => showManual = false;
        var steps = new StackPanel { Margin = new Thickness(6, 6, 0, 0) };
        string[] lines =
        [
            "1.  Click “Open the StarNet2 Website”.",
            "2.  In the Windows section, download the ZIP of the command-line version (or the installer, setup.exe).",
            "3.  Open your Downloads folder. A ZIP does not need to be unpacked.",
            $"4.  Come back here, click “I already have StarNet2…” and choose the ZIP, or the file called {StarNet.ExecutableName} inside the unpacked folder.",
        ];
        foreach (var l in lines) steps.Children.Add(new TextBlock { Text = l, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        steps.Children.Add(new TextBlock { Text = "If you used the StarNet2 installer, CombineColors finds it by itself — just open this window again.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
        exp.Content = steps;
        content.Children.Add(exp);
    }

    void Downloading()
    {
        Text($"Downloading StarNet2{(setup.Latest is StarNetRelease l ? " " + l.Version : "")} from starnetastro.com…");
        var d = setup.Download;
        var bar = new ProgressBar { Height = 14, Margin = new Thickness(0, 6, 0, 6), IsIndeterminate = d.Total <= 0, Maximum = 1, Value = d.Fraction };
        content.Children.Add(bar);
        if (d.Total > 0) Caption($"{Mb(d.Received)} of {Mb(d.Total)}");
        Footer(Btn("Cancel", setup.CancelDownload, cancel: true));
    }

    void License()
    {
        Text($"StarNet2{(setup.Info is StarNetInfo i ? " " + i.Version : "")} is installed. Before CombineColors uses it, please read its license. In short: it is free for astrophotography, your images remain yours, and the program must not be passed on to others.");
        if (setup.LicenseText is string text)
        {
            var tb = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11, Height = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 6) };
            content.Children.Add(tb);
        }
        else
        {
            Caption("The license file was not found next to the program. Please read it on the StarNet2 website.");
            content.Children.Add(Row(Btn("Read the License on the Website", OpenWebsite)));
        }
        var cb = new CheckBox { Content = "I have read and I accept the StarNet2 license agreement", IsChecked = accepted, Margin = new Thickness(0, 6, 0, 0) };
        var cont = DialogBase.Default("Continue", setup.AcceptLicense);
        cont.IsEnabled = accepted;
        cb.Click += (_, _) => { accepted = cb.IsChecked == true; cont.IsEnabled = accepted; };
        content.Children.Add(cb);
        Footer(Btn("Cancel", () => w.Close(), cancel: true), cont);
    }

    void Failed(string message)
    {
        content.Children.Add(new TextBlock { Text = "⚠  " + message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(Row(Btn("Try Again", setup.StartDownload), Btn("Choose the Program…", Locate), Btn("Open the Website", OpenWebsite)));
        Manual("How to install it yourself");
        Footer(Close());
    }

    // ---- Ready -------------------------------------------------------------------------------------

    void Ready()
    {
        var items = model.StarRemovalItems(folder);
        var chosen = items.Where(i => selected.Contains(i.Id)).ToList();
        if (setup.Info is StarNetInfo info)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (setup.UpdateAvailable && setup.Latest is StarNetRelease l)
            {
                var up = Btn($"Update to {l.Version}", setup.StartDownload); DockPanel.SetDock(up, Dock.Right); row.Children.Add(up);
            }
            row.Children.Add(new TextBlock { Text = $"✔  StarNet2 {info.Version} is ready{(string.IsNullOrEmpty(info.Backend) ? "" : $" ({info.Backend})")}.", Foreground = Brushes.LightGreen, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(row);
            GpuPanel();
        }
        if (items.Count == 0)
        {
            Caption("Load your channels first, then open this window again.");
            Footer(Close());
            return;
        }
        content.Children.Add(new TextBlock { Text = "Choose the images to clean:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var listBox = new Border { Child = list, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), Margin = new Thickness(0, 0, 0, 8) };
        Button? run = null;
        foreach (var item in items)
        {
            var it = item;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var k in it.Kinds)
                row.Children.Add(new Border { Background = Ui.Tint(k), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 3, 0), Child = new TextBlock { Text = k.ShortName(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White } });
            row.Children.Add(new TextBlock { Text = Path.GetFileName(it.Source), Margin = new Thickness(5, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 });
            if (it.AlreadyStarless) row.Children.Add(new TextBlock { Text = "  already without stars", Foreground = Brushes.Gray });
            var cb = new CheckBox { Content = row, IsChecked = selected.Contains(it.Id), Margin = new Thickness(0, 2, 0, 2) };
            cb.Click += (_, _) => { if (cb.IsChecked == true) selected.Add(it.Id); else selected.Remove(it.Id); Refresh(); };
            list.Children.Add(cb);
        }
        content.Children.Add(listBox);
        var noFile = model.ChannelsWithoutFile;
        if (noFile.Count > 0) Caption($"{string.Join(", ", noFile.Select(k => k.ShortName()))} {(noFile.Count == 1 ? "has" : "have")} no file of its own and stays as it is.");
        Text($"For each image, two new files are saved {(folder == null ? "next to it" : "in " + Path.GetFileName(folder))}: one ending in _bg (the image without stars) and one ending in _stars (only the stars). The channels then switch to the _bg files and the stars go to the Stars tab. Your original files are not changed — File ▸ Open Recent Set brings them back.");
        if (chosen.Any(i => i.OutputsExist)) Warn("_bg / _stars files from an earlier run will be replaced.");
        if (model.EditingTool != null) Warn("Close the open editor (Dust, Gradient or Manual) first.");
        var exp = new Expander { Header = "Options", IsExpanded = showOptions, Margin = new Thickness(0, 6, 0, 0) };
        exp.Expanded += (_, _) => showOptions = true; exp.Collapsed += (_, _) => showOptions = false;
        var opts = new StackPanel { Margin = new Thickness(6, 6, 0, 0) };
        opts.Children.Add(new TextBlock { Text = "Brighten linear images with", Margin = new Thickness(0, 0, 0, 4) });
        foreach (var m in StarRemovalNames.AllMethods)
        {
            var mm = m;
            var rb = new RadioButton { Content = m.DisplayName(), IsChecked = method == m, GroupName = "starRemovalMethod", Margin = new Thickness(8, 1, 0, 1) };
            rb.Checked += (_, _) => { method = mm; model.Preferences.Set("starRemovalMethod", mm.RawValue()); };
            opts.Children.Add(rb);
        }
        opts.Children.Add(new TextBlock { Text = "StarNet2 was trained on stretched images. Linear data are stretched for it and the result is brought back to linear, so the normalisation and stretch of this project keep working. Images that already look stretched are processed as they are.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 4, 0, 8) });
        var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var change = Btn("Change…", () => { if (Panels.OpenFolder(w, "Choose where to save the _bg and _stars files", folder) is string f) { folder = f; Refresh(); } });
        DockPanel.SetDock(change, Dock.Right);
        if (folder != null) { var rs = Btn("Reset", () => { folder = null; Refresh(); }); DockPanel.SetDock(rs, Dock.Right); folderRow.Children.Add(rs); }
        folderRow.Children.Add(change);
        folderRow.Children.Add(new TextBlock { Text = "Save the new files:  " + (folder ?? "next to the originals"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        opts.Children.Add(folderRow);
        opts.Children.Add(Row(Btn("Use a Different StarNet2 Program…", Locate)));
        if (setup.Gpu == StarNetSetupModel.GpuPhase.Declined)
            opts.Children.Add(Row(Btn($"Download DirectML {DirectMLPackage.Default.Version} for the Graphics Card…", setup.InstallDirectML)));
        exp.Content = opts;
        content.Children.Add(exp);
        Caption("Each image takes about a minute (longer without a graphics card StarNet2 can use).");
        run = DialogBase.Default(chosen.Count > 1 ? $"☆ Remove Stars from {chosen.Count} Images" : "☆ Remove Stars",
            () => model.RemoveStars(chosen, new StarRemovalOptions(method, model.Settings.Stretch.Clone()), folder));
        run.IsEnabled = chosen.Count > 0 && model.EditingTool == null && !model.IsLoading;
        Footer(Close(), run);
    }

    /// <summary>StarNet2 cannot use the graphics card with Windows' old DirectML: offer Microsoft's current one.</summary>
    void GpuPanel()
    {
        var st = setup.DirectMLStatus;
        string available = DirectMLPackage.Default.Version.ToString();
        switch (setup.Gpu)
        {
            case StarNetSetupModel.GpuPhase.Offer:
            case StarNetSetupModel.GpuPhase.Failed:
            {
                var box = Panel();
                Add(box, new TextBlock { Text = "⚡  StarNet2 cannot use your graphics card yet", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
                Add(box, Wrap($"It needs Microsoft DirectML {available} or newer, and Windows only has version {st?.Found?.ToString() ?? "(none)"}. Without it StarNet2 runs on the processor, several times slower."));
                Add(box, Wrap($"CombineColors can download DirectML {available} from Microsoft (about {Mb(DirectMLPackage.Default.EntrySize)}, from nuget.org) and put it in the StarNet2 folder. Nothing else on the computer changes; deleting DirectML.dll from that folder undoes it. Microsoft's DirectML license applies.", gray: true));
                if (setup.Gpu == StarNetSetupModel.GpuPhase.Failed && setup.DirectMLFailure is string f)
                    Add(box, new TextBlock { Text = "⚠  " + f, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, Margin = new Thickness(0, 2, 0, 6) });
                var dl = Btn(setup.Gpu == StarNetSetupModel.GpuPhase.Failed ? "Try Again" : $"Download DirectML {available}", setup.InstallDirectML);
                dl.Background = (Brush)Application.Current.Resources["AccentBrush"]; dl.Foreground = Brushes.White;
                Add(box, Row(dl, Btn("Not Now", setup.DeclineDirectML), Btn("License", () => Open(DirectMLPackage.LicenseUrl))));
                break;
            }
            case StarNetSetupModel.GpuPhase.Downloading:
            {
                var box = Panel();
                var d = setup.DirectMLDownload;
                Add(box, Wrap($"Downloading DirectML {available} from Microsoft…"));
                var bar = new ProgressBar { Height = 12, Maximum = 1, Value = d.Fraction, IsIndeterminate = d.Total <= 0, Margin = new Thickness(0, 4, 0, 4) };
                Add(box, bar);
                Add(box, Row(Btn("Cancel", setup.CancelDirectML)));
                break;
            }
            case StarNetSetupModel.GpuPhase.Installed:
                content.Children.Add(new TextBlock { Text = $"✔  DirectML {st?.Found?.ToString() ?? available} is installed next to StarNet2: it can use the graphics card now.", Foreground = Brushes.LightGreen, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
                break;
        }

        StackPanel Panel()
        {
            var inner = new StackPanel();
            content.Children.Add(new Border { Child = inner, Padding = new Thickness(12, 10, 12, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["AccentBrush"], Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), Margin = new Thickness(0, 0, 0, 10) });
            return inner;
        }
        static void Add(StackPanel p, UIElement e) => p.Children.Add(e);
        static TextBlock Wrap(string t, bool gray = false) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = gray ? Brushes.Gray : (Brush)Application.Current.Resources["TextBrush"], FontSize = gray ? 12.5 : 13.5 };
    }

    static void Open(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }); } catch { }
    }

    // ---- Running -----------------------------------------------------------------------------------

    void Running(StarRemovalProgress p)
    {
        wasRemoving = true;
        content.Children.Add(new TextBlock { Text = "Removing stars…", FontWeight = FontWeights.Bold });
        content.Children.Add(new TextBlock { Text = p.Label, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 4) });
        content.Children.Add(new ProgressBar { Height = 14, Maximum = 1, Value = p.Fraction, Margin = new Thickness(0, 4, 0, 4) });
        Caption(p.Remaining.Length == 0 ? "Starting…" : p.Remaining);
        Caption("You can keep this window open; the channels switch to the new files when it is done.");
        Footer(Btn("Stop", model.StopStarRemoval, cancel: true));
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    void Text(string t) => content.Children.Add(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
    void Caption(string t) => content.Children.Add(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) });
    void Warn(string t) => content.Children.Add(new TextBlock { Text = "⚠  " + t, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) });

    void Busy(string t)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 80, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(row);
    }

    static Button Btn(string text, Action a, bool cancel = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), IsCancel = cancel };
        b.Click += (_, _) => a();
        return b;
    }

    static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    Button Close() => Btn("Close", () => w.Close(), cancel: true);

    void Footer(Button left, Button? right = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        left.Margin = new Thickness(0);
        d.Children.Add(left);
        if (right != null) { DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); }
        content.Children.Add(d);
    }

    string SizeLabel => (setup.Latest?.SizeBytes ?? StarNetRelease.Fallback().SizeBytes) is long s ? Mb(s) : "190 MB";
    static string Mb(long bytes) => $"{(int)Math.Round(bytes / 1_000_000.0)} MB";

    static void OpenWebsite()
    {
        try { Process.Start(new ProcessStartInfo(StarNet.Website.ToString()) { UseShellExecute = true }); } catch { }
    }

    void Locate()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the file called “{StarNet.ExecutableName}” (or the StarNet2 ZIP you downloaded)",
            Filter = $"StarNet2 program or ZIP|{StarNet.ExecutableName};*.zip|All files|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (d.ShowDialog(w) == true) setup.Choose(d.FileName);
    }
}
