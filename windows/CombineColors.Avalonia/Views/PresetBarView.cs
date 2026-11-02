using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CombineColors.Model;

namespace CombineColors.Views;

/// <summary>Bar under the palettes: saved presets as thumbnails, one click to switch.</summary>
public sealed class PresetBarView : DockPanel
{
    readonly ProjectModel model;
    readonly Window owner;
    readonly StackPanel row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
    readonly Button save;

    public PresetBarView(ProjectModel model, Window owner)
    {
        this.model = model; this.owner = owner;
        Height = 78;
        Background = AppTheme.PanelBackground;
        save = new Button { Width = 70, Height = 62, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(save, "Remember the current settings with a thumbnail");
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "+", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "Save preset", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
        save.Content = content;
        save.Click += (_, _) => model.SavePreset();
        Children.Add(save);
        Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = row, VerticalAlignment = VerticalAlignment.Center });
        model.Changed += what => { if (what is Change.Presets or Change.Settings or Change.Sources or Change.Frame) Rebuild(); };
        Rebuild();
    }

    void Rebuild()
    {
        save.IsEnabled = model.Sources.Count > 0 && model.Frame != null;
        row.Children.Clear();
        var active = model.ActivePresetId;
        foreach (var p in model.Presets)
        {
            bool isActive = active == p.Id;
            var cell = new StackPanel { Width = 84, Margin = new Thickness(0, 0, 6, 0), Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(cell, p.Name + " — click to apply, right-click for options");
            var frame = new Border
            {
                Width = 84, Height = 50, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), ClipToBounds = true,
                BorderBrush = isActive ? AppTheme.AccentBrush : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderThickness = new Thickness(isActive ? 2 : 1),
            };
            if (model.PresetThumbnail(p) is { } img) frame.Child = new Image { Source = img, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
            cell.Children.Add(frame);
            cell.Children.Add(new TextBlock { Text = p.Name, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Foreground = isActive ? AppTheme.AccentBrush : AppTheme.TextBrush });
            var preset = p;
            cell.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) model.ApplyPreset(preset); };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += async (_, _) => { if (await RenameWindow.Ask(owner, "Rename preset", preset.Name) is string n) model.RenamePreset(preset, n); };
            var update = new MenuItem { Header = "Update with current settings" }; update.Click += (_, _) => model.UpdatePreset(preset);
            var delete = new MenuItem { Header = "Delete" }; delete.Click += (_, _) => model.DeletePreset(preset);
            menu.Items.Add(rename); menu.Items.Add(update); menu.Items.Add(new Separator()); menu.Items.Add(delete);
            cell.ContextMenu = menu;
            row.Children.Add(cell);
        }
        if (model.Presets.Count == 0)
            row.Children.Add(new TextBlock { Text = "Save a preset to come back to a look later, then click thumbnails to compare them.", FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
    }
}
