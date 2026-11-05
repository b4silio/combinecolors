using System.Windows;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Tests;

/// <summary>
/// Straightening in the crop editor (the picture turns under the upright crop on Apply) and bright spots in the
/// dust editor, in the Windows app model (the same code as the Linux one, from the Mac app's 1.17–1.19).
/// </summary>
public class CropTurnModelTests : IDisposable
{
    readonly ModelFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public void TurningWithTheCropResamplesOnApplyAndGoesBackLossless()
    {
        var m = f.LoadedModel();
        var haBefore = m.Sources[ChannelKind.Ha].Image;
        m.BeginDustEditing(ChannelKind.Ha);
        m.AddDustSpot(new Point(150, 40));
        var spot = m.DustDraft!.SpotsFor(ChannelKind.Ha)[0];
        m.CommitDustEditing();
        Assert.True(f.Wait(() => !m.IsLoading && m.Context != null && m.Dust.SpotsFor(ChannelKind.Ha).Count == 1));
        var healedBefore = m.Sources[ChannelKind.Ha].Image;

        // Turning while editing changes nothing until Apply; the constraint keeps the crop inside the picture.
        m.BeginCropEditing();
        m.CropDraft = new PixelRect(0, 0, 200, 150);
        m.SetCropAngle(10);
        Assert.Equal(0, m.Orientation.Angle);
        Assert.Same(healedBefore, m.Sources[ChannelKind.Ha].Image);
        var draft = m.CropDraft!.Value;
        Assert.True(draft.Width < 200 && draft.Height < 150);
        Assert.InRange((double)draft.Width / draft.Height, 200.0 / 150 - 0.03, 200.0 / 150 + 0.03);
        m.SetCropAngle(0);
        Assert.Equal(new PixelRect(0, 0, 200, 150), m.CropDraft);   // grows back with the angle
        m.SetCropAngle(10);
        m.CommitCropEditing();
        Assert.True(f.Wait(() => m.Orientation.Angle == 10 && !m.IsLoading && m.Context != null && !ReferenceEquals(m.Sources[ChannelKind.Ha].Image, healedBefore)));
        Assert.Equal(new Orientation(0, false, 10), m.Orientation);
        Assert.Equal(draft, m.Crop);
        Assert.Equal(new Size(200, 150), m.ImageSize);
        // The spot turned with the picture; the centre pixel is the pivot; a corner comes in empty.
        var turned = m.Dust.SpotsFor(ChannelKind.Ha)[0];
        var e = FineRotation.Rotate(spot.X, spot.Y, 10, 200, 150);
        Assert.Equal(e.X, turned.X, 9); Assert.Equal(e.Y, turned.Y, 9);
        Assert.Equal(0f, m.LayerSources(ImageLayer.Background)[ChannelKind.Ha].Image[0, 0]);
        Assert.Equal(10, m.ProjectDocument().Angle);

        // Reopening the project turns the files the same way.
        var path = Path.Combine(f.Dir, "turned.json");
        m.SaveProject(path);
        var m2 = f.NewModel();
        m2.OpenProject(path);
        Assert.True(f.Wait(() => m2.Sources.Count == 3 && !m2.IsLoading && m2.Context != null));
        Assert.Equal(10, m2.Orientation.Angle);
        Assert.Equal(m.Sources[ChannelKind.OIII].Image.Data, m2.Sources[ChannelKind.OIII].Image.Data);

        // A quarter turn keeps the fine angle; back to 0 gives the untouched (quarter-turned) channel.
        m.RotateClockwise();
        Assert.True(f.Wait(() => m.Orientation.Rotation == 1 && !m.IsLoading && m.Context != null));
        Assert.Equal(10, m.Orientation.Angle);
        m.BeginCropEditing();
        m.SetCropAngle(0);
        m.CommitCropEditing();
        Assert.True(f.Wait(() => m.Orientation.Angle == 0 && !m.IsLoading && m.Context != null));
        var raw = haBefore.Rotated90CW(1);
        Assert.Equal(raw[20, 20], m.Sources[ChannelKind.Ha].Image[20, 20], 5);
    }

    [Fact]
    public void QuarterTurnsBecomeExactOnes()
    {
        var m = f.LoadedModel();
        var before = m.Sources[ChannelKind.OIII].Image;
        m.BeginCropEditing();
        m.SetCropAngle(270);
        Assert.Equal(-90, m.CropAngleDraft);   // kept in −180…180
        m.SetCropAngle(180);
        Assert.Equal((0.0, 0.0, 200.0, 150.0), m.CropEditBounds);
        m.CropDraft = new PixelRect(0, 0, 200, 150);
        m.CommitCropEditing();
        Assert.True(f.Wait(() => m.Orientation.Rotation == 2 && !m.IsLoading && m.Context != null));
        Assert.Equal(new Orientation(2, false, 0), m.Orientation);
        Assert.Equal(before.Rotated90CW(2).Data, m.Sources[ChannelKind.OIII].Image.Data);
    }

    [Fact]
    public void BrightSpotsStayWithinTheMaxRadius()
    {
        var m = f.LoadedModel();
        m.BeginDustEditing(ChannelKind.Ha);
        m.UpdateDustDefaults(d => d.Polarity = SpotPolarity.Bright);
        m.DustBrightMaxRadius = 9;
        Assert.Equal(9, m.DustBrightMaxRadius);
        m.DustBrightMaxRadius = 1;
        Assert.Equal(4, m.DustBrightMaxRadius);   // never below 4 px
        m.DustBrightMaxRadius = 9;
        m.AddDustSpot(new Point(100, 75));
        var s = Assert.Single(m.DustDraft!.SpotsFor(ChannelKind.Ha));
        Assert.Equal(SpotPolarity.Bright, s.Polarity);
        Assert.True(s.Radius <= 9, $"radius {s.Radius}");
        m.CommitDustEditing();
        Assert.True(f.Wait(() => !m.IsLoading && m.Context != null));
        Assert.Equal(SpotPolarity.Bright, m.ProjectDocument().Dust.SpotsFor(ChannelKind.Ha)[0].Polarity);
    }
}
