using System.Diagnostics;
using AstroCore;

namespace AstroCore.Tests;

/// <summary>
/// A stand-in for the StarNet2 program (the real one is never run by the tests): a batch file on Windows,
/// a shell script elsewhere, that answers <c>--machine-info</c>, logs its arguments, emits progress lines and
/// copies the input to every requested output.
/// </summary>
public static class FakeStarNet
{
    public enum Behaviour { Copy, Fail, Slow }

    public static string Make(string dir, Behaviour behaviour = Behaviour.Copy)
    {
        Directory.CreateDirectory(dir);
        return OperatingSystem.IsWindows() ? MakeBatch(dir, behaviour) : MakeShell(dir, behaviour);
    }

    static string MakeBatch(string dir, Behaviour behaviour)
    {
        var exe = Path.Combine(dir, "starnet2.cmd");
        var before = behaviour switch
        {
            Behaviour.Fail => "echo {\"schema\":\"starnetastro.cli.progress.v1\",\"event\":\"error\",\"message\":\"model could not be loaded\"} 1>&2\r\nexit /b 3",
            Behaviour.Slow => "ping -n 21 127.0.0.1 >nul",
            _ => "",
        };
        var script = string.Join("\r\n",
            "@echo off",
            "setlocal EnableExtensions DisableDelayedExpansion",
            ">> \"%~dp0calls.log\" echo %*",
            "if \"%~1\"==\"--machine-info\" (",
            "  echo {\"schema\":\"starnetastro.cli.machine-info.v1\",\"product\":\"starnet2\",\"version\":\"2.6.2\",\"build\":\"0242\",\"backend\":{\"id\":\"ort\",\"display_name\":\"ONNX Runtime\"}}",
            "  exit /b 0",
            ")",
            "set \"IN=\"", "set \"OUT=\"", "set \"MASK=\"", "set \"UNS=\"",
            ":parse",
            "if \"%~1\"==\"\" goto parsed",
            "if \"%~1\"==\"-i\" set \"IN=%~2\"",
            "if \"%~1\"==\"-o\" set \"OUT=%~2\"",
            "if \"%~1\"==\"--mask\" set \"MASK=%~2\"",
            "if \"%~1\"==\"--unscreen\" set \"UNS=%~2\"",
            "shift",
            "goto parse",
            ":parsed",
            before,
            "echo {\"schema\":\"starnetastro.cli.diagnostic.v1\",\"severity\":\"info\",\"code\":\"input\",\"message\":\"Reading\"} 1>&2",
            "echo {\"schema\":\"starnetastro.cli.progress.v1\",\"event\":\"progress\",\"stage\":\"inference\",\"current\":1,\"total\":2,\"percent\":50.0} 1>&2",
            "copy /y \"%IN%\" \"%OUT%\" >nul",
            "if defined MASK copy /y \"%IN%\" \"%MASK%\" >nul",
            "if defined UNS copy /y \"%IN%\" \"%UNS%\" >nul",
            "echo {\"schema\":\"starnetastro.cli.progress.v1\",\"event\":\"finish\",\"stage\":\"inference\",\"current\":2,\"total\":2,\"percent\":100.0,\"ok\":true} 1>&2",
            "exit /b 0",
            "");
        File.WriteAllText(exe, script);
        return exe;
    }

    static string MakeShell(string dir, Behaviour behaviour)
    {
        var exe = Path.Combine(dir, "starnet2");
        var before = behaviour switch
        {
            Behaviour.Fail => "echo '{\"schema\":\"starnetastro.cli.progress.v1\",\"event\":\"error\",\"message\":\"model could not be loaded\"}' >&2; exit 3",
            Behaviour.Slow => "sleep 20",
            _ => "",
        };
        var script = string.Join("\n",
            "#!/bin/bash",
            "echo \"$@\" >> \"$(dirname \"$0\")/calls.log\"",
            "if [ \"$1\" = \"--machine-info\" ]; then",
            "  echo '{\"schema\":\"starnetastro.cli.machine-info.v1\",\"product\":\"starnet2\",\"version\":\"2.6.2\",\"build\":\"0242\",\"backend\":{\"id\":\"ort\",\"display_name\":\"ONNX Runtime\"}}'",
            "  exit 0",
            "fi",
            "in=\"\"; out=\"\"; mask=\"\"; uns=\"\"",
            "while [ $# -gt 0 ]; do",
            "  case \"$1\" in",
            "    -i) in=\"$2\"; shift;;",
            "    -o) out=\"$2\"; shift;;",
            "    --mask) mask=\"$2\"; shift;;",
            "    --unscreen) uns=\"$2\"; shift;;",
            "  esac",
            "  shift",
            "done",
            before,
            "echo '{\"schema\":\"starnetastro.cli.diagnostic.v1\",\"severity\":\"info\",\"code\":\"input\",\"message\":\"Reading\"}' >&2",
            "echo '{\"schema\":\"starnetastro.cli.progress.v1\",\"event\":\"progress\",\"stage\":\"inference\",\"current\":1,\"total\":2,\"percent\":50.0}' >&2",
            "cp \"$in\" \"$out\"",
            "[ -n \"$mask\" ] && cp \"$in\" \"$mask\"",
            "[ -n \"$uns\" ] && cp \"$in\" \"$uns\"",
            "echo '{\"schema\":\"starnetastro.cli.progress.v1\",\"event\":\"finish\",\"stage\":\"inference\",\"current\":2,\"total\":2,\"percent\":100.0,\"ok\":true}' >&2",
            "exit 0",
            "");
        File.WriteAllText(exe, script);
        MakeExecutable(exe);
        return exe;
    }

    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    public static string[] Calls(string dir)
    {
        var log = Path.Combine(dir, "calls.log");
        return File.Exists(log) ? File.ReadAllLines(log).Where(l => l.Trim().Length > 0).ToArray() : [];
    }
}

public sealed class StarNetTests : IDisposable
{
    readonly string dir;

    public StarNetTests()
    {
        // A space in the path checks the argument quoting.
        dir = Path.Combine(Path.GetTempPath(), $"StarNetTests {Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static StarRemovalOptions Defaults() => new();

    // MARK: Pure parts

    [Fact]
    public void CommandArguments()
    {
        var c = new StarNetCommand(@"C:\a b\in.fits", @"C:\o\out.fits");
        Assert.Equal(new[] { "--machine-progress", "-i", @"C:\a b\in.fits", "-o", @"C:\o\out.fits" }, c.Arguments);
        c.Mask = @"C:\o\stars.fits";
        c.Linear = true;
        c.ShadowsClipping = 0.5f;          // StarNet2 rejects a positive factor
        c.TargetBackground = 0.2f;
        c.Upsample = true;
        Assert.Equal(new[] { "--machine-progress", "-i", @"C:\a b\in.fits", "-o", @"C:\o\out.fits", "--mask", @"C:\o\stars.fits",
                             "--linear", "--shadows-clipping", "-0.010", "--target-background", "0.200", "--upsample" }, c.Arguments);
        // Linear-only options are left out without --linear.
        c.Linear = false;
        Assert.DoesNotContain("--target-background", c.Arguments);
    }

    [Fact]
    public void MachineInfoAndFeedParsing()
    {
        var info = StarNetInfo.Parse("""{"product":"starnet2","version":"2.6.2","build":"0242","backend":{"display_name":"ONNX Runtime"}}""");
        Assert.Equal(new StarNetInfo("2.6.2", "0242", "ONNX Runtime"), info);
        Assert.Null(StarNetInfo.Parse("""{"product":"deepsnr","version":"1.3.2"}"""));
        Assert.Null(StarNetInfo.Parse("starnet2 version 2.6.2"));

        var feed = """
        {"schema":"starnetastro.cli-tools.latest.v1","tools":{"starnet2":{"latest":{
          "windows-x64":{"version":"2.7.0","packages":{"archive":{"url":"https://download.starnetastro.com/a.zip","sha256":"abc","size_bytes":123}}},
          "linux-x64":{"version":"2.7.0","packages":{"archive":{"url":"http://insecure.example/b.zip"}}}}}}}
        """;
        var r = StarNetRelease.ParseFeed(feed, "windows-x64");
        Assert.Equal(new StarNetRelease("2.7.0", new Uri("https://download.starnetastro.com/a.zip"), "abc", 123), r);
        Assert.Equal("a.zip", r!.FileName);
        Assert.Null(StarNetRelease.ParseFeed(feed, "linux-x64"));   // only https links are taken
        Assert.Null(StarNetRelease.ParseFeed("{}"));
        Assert.Null(StarNetRelease.ParseFeed("not json"));
        Assert.Contains("starnet2_win_", StarNetRelease.Fallback("windows-x64").Url.AbsoluteUri);
        Assert.Equal(192_822_891, StarNetRelease.Fallback("windows-x64").SizeBytes);
        Assert.Contains("starnet2_linux_", StarNetRelease.Fallback("linux-x64").Url.AbsoluteUri);
        Assert.Contains("COREML_arm64", StarNetRelease.Fallback("macos-arm64").Url.AbsoluteUri);
        Assert.Equal(OperatingSystem.IsWindows() ? "windows-x64" : OperatingSystem.IsLinux() ? "linux-x64" : StarNet.PlatformKey, StarNet.PlatformKey);

        Assert.True(StarNet.IsVersionNewer("2.6.10", "2.6.2"));
        Assert.True(StarNet.IsVersionNewer("3", "2.9.9"));
        Assert.False(StarNet.IsVersionNewer("2.6.2", "2.6.2"));
        Assert.False(StarNet.IsVersionNewer("2.6", "2.6.0"));
        Assert.True(StarNetSetup.IsUpdateAvailable(new StarNetInfo("2.6.2", "", ""), r));
    }

    [Fact]
    public void ProgressEvents()
    {
        Assert.Equal(new StarNetEvent.Progress(0.425), StarNetEvent.Parse("""{"schema":"starnetastro.cli.progress.v1","event":"progress","percent":42.5}"""));
        Assert.Equal(new StarNetEvent.Message("boom"), StarNetEvent.Parse("""{"schema":"starnetastro.cli.progress.v1","event":"error","message":"boom"}"""));
        Assert.Equal(new StarNetEvent.Message("clipped"), StarNetEvent.Parse("""{"schema":"starnetastro.cli.diagnostic.v1","severity":"warning","message":"clipped"}"""));
        Assert.Equal(new StarNetEvent.Other(), StarNetEvent.Parse("""{"schema":"starnetastro.cli.diagnostic.v1","severity":"info","message":"x"}"""));
        Assert.Null(StarNetEvent.Parse("12 %"));
    }

    [Fact]
    public void OutputNames()
    {
        var data = Path.Combine(dir, "data");
        var fits = StarRemoval.OutputPaths(Path.Combine(data, "M42-Ha.fit"));
        Assert.Equal(Path.Combine(data, "M42-Ha_bg.fits"), fits.Starless);
        Assert.Equal(Path.Combine(data, "M42-Ha_stars.fits"), fits.Stars);
        Assert.Equal("Stack_Ha_bg.fits", Path.GetFileName(StarRemoval.OutputPaths(Path.Combine(data, "Stack_Ha.xisf")).Starless));
        var outDir = Path.Combine(dir, "out");
        var tif = StarRemoval.OutputPaths(Path.Combine(data, "rgb.png"), outDir);
        Assert.Equal(Path.Combine(outDir, "rgb_bg.tif"), tif.Starless);
        Assert.Equal(Path.Combine(outDir, "rgb_stars.tif"), tif.Stars);
        Assert.True(StarRemoval.IsStarless(Path.Combine(data, "M42-Ha_bg.fits")));
        Assert.False(StarRemoval.IsStarless(Path.Combine(data, "M42-Ha.fits")));
    }

    [Fact]
    public void InverseStretchRoundTripsEveryMethod()
    {
        var stats = Statistics.Compute(Synth.Channel());
        foreach (var method in StretchMethods.All)
        {
            var s = new StretchSettings { Method = method };
            if (method == StretchMethod.Mtf) { s.MtfShadows = 0.05f; s.MtfMidtones = 0.2f; s.MtfHighlights = 0.9f; }
            if (method == StretchMethod.Ghs) s.GhsSP = 0.02f;
            s.Gamma = method == StretchMethod.Log ? 0.8f : 1;
            var r = Stretch.Resolve(s, stats);
            var inv = new InverseStretch(r);
            float lo = method == StretchMethod.None ? 0 : r.C0, hi = method == StretchMethod.None ? 1 : r.C1;
            // The inverse is only defined where the stretch is not clipped (between the black and white points;
            // for the manual MTF also inside its own shadows / highlights).
            float a = method == StretchMethod.Mtf ? lo + 0.05f * (hi - lo) : lo, b = method == StretchMethod.Mtf ? lo + 0.9f * (hi - lo) : hi;
            float worst = 0;
            for (int i = 1; i < 200; i++)
            {
                float x = a + (b - a) * i / 200;
                float back = inv.Evaluate(r.Evaluate(x));
                worst = MathF.Max(worst, MathF.Abs(back - x) / MathF.Max(hi - lo, 1e-6f));
            }
            Assert.True(worst < 2e-3f, $"{method}: worst {worst}");
            // Background-level values come back much closer (the stretch is steep there).
            float bg = stats.Median;
            if (bg > lo && bg < hi && method != StretchMethod.Mtf) A.Close(bg, inv.Evaluate(r.Evaluate(bg)), 2e-5f * MathF.Max(hi - lo, 1e-3f), method.ToString());
        }
    }

    // MARK: Runs with the stand-in program

    [Fact]
    public void ToolInfoAndFailures()
    {
        var tool = new StarNetTool(FakeStarNet.Make(dir));
        var info = tool.Info();
        Assert.Equal("2.6.2", info.Version);
        Assert.Equal("ONNX Runtime", info.Backend);

        var notStarNet = Path.Combine(dir, OperatingSystem.IsWindows() ? "other.cmd" : "other");
        File.WriteAllText(notStarNet, OperatingSystem.IsWindows() ? "@echo hello\r\n" : "#!/bin/bash\necho hello\n");
        FakeStarNet.MakeExecutable(notStarNet);
        Assert.Equal(StarNetErrorKind.NotStarNet2, Assert.Throws<StarNetError>(() => new StarNetTool(notStarNet).Info()).Kind);
        Assert.Equal(StarNetErrorKind.NotStarNet2, Assert.Throws<StarNetError>(() => new StarNetTool(Path.Combine(dir, "missing.exe")).Info()).Kind);

        var failDir = Path.Combine(dir, "fail");
        var failing = new StarNetTool(FakeStarNet.Make(failDir, FakeStarNet.Behaviour.Fail));
        var e = Assert.Throws<StarNetError>(() => StarRemoval.Run([Synth.Channel(512, 512)], Defaults(), failing, dir));
        Assert.Equal(StarNetErrorKind.Failed, e.Kind);
        Assert.Equal(3, e.Status);
        Assert.Contains("model could not be loaded", e.Message);
    }

    [Fact]
    public void LinearRunUsesStarNetLinearModeWithProjectValues()
    {
        var exe = FakeStarNet.Make(dir);
        var plane = Synth.Channel(512, 520);
        var opts = Defaults();
        opts.Stretch.TargetBackground = 0.2f;
        opts.Stretch.ShadowsClipping = -2.8f;
        var progress = new List<double>();
        var r = StarRemoval.Run([plane], opts, new StarNetTool(exe), dir, p => { lock (progress) progress.Add(p); });
        Assert.Equal(StarRemovalProcessing.StarnetLinear, r.Processing);
        Assert.Single(r.Starless);
        Assert.Equal(plane.ToArray(), r.Starless[0].ToArray());   // the stand-in copies its input: the data reach StarNet2 unscaled
        var call = FakeStarNet.Calls(dir).Last();
        Assert.Contains("--linear --shadows-clipping -2.800 --target-background 0.200", call);
        Assert.Contains("--mask", call);
        Assert.DoesNotContain("--unscreen", call);
        Assert.Equal(new[] { 0.5, 1.0 }, progress);
    }

    [Fact]
    public void FloatADUDataAreScaledForStarNetAndBack()
    {
        var exe = FakeStarNet.Make(dir);
        var plane = Synth.Channel(512, 512);
        plane.Affine(65535, 0);
        var r = StarRemoval.Run([plane], Defaults(), new StarNetTool(exe), dir);
        // What StarNet2 got was normalised to [0, 1].
        var sent = FitsReader.Read(Path.Combine(dir, "input.fits")).Planes[0];
        Assert.True(sent.MinMax().max <= 1);
        A.Close(plane.MinMax().max, r.Starless[0].MinMax().max, 0.05f);
    }

    [Fact]
    public void AppStretchIsUndoneAfterwards()
    {
        var exe = FakeStarNet.Make(dir);
        var plane = Synth.Channel(512, 512);
        foreach (var method in new[] { StretchMethod.AutoSTF, StretchMethod.Arcsinh, StretchMethod.Ghs })
        {
            var opts = new StarRemovalOptions(StarRemovalMethod.AppStretch);
            opts.Stretch.Method = method;
            var r = StarRemoval.Run([plane], opts, new StarNetTool(exe), dir);
            Assert.Equal(StarRemovalProcessing.AppStretch, r.Processing);
            // StarNet2 received stretched data…
            var sent = FitsReader.Read(Path.Combine(dir, "input.fits")).Planes[0];
            Assert.True(Statistics.Compute(sent).Median > 0.05f, $"{method}");
            // …and an unchanged (copied) result comes back as the original linear data, with nothing left as stars
            // except where the stretch clipped the highlights.
            var s = Statistics.Compute(r.Starless[0]); var o = Statistics.Compute(plane);
            A.Close(o.Median, s.Median, o.Madn * 0.02f, method.ToString());
            Assert.True(Statistics.Compute(r.Stars[0]).Percentile(0.99) < o.Madn * 0.05f, $"{method}");
            Assert.DoesNotContain("--linear", FakeStarNet.Calls(dir).Last());
        }
    }

    [Fact]
    public void StretchedDataAreProcessedAsTheyAre()
    {
        var exe = FakeStarNet.Make(dir);
        var plane = Synth.Channel(512, 512, background: 0.2f, nebula: 0.3f);
        var r = StarRemoval.Run([plane, plane, plane], Defaults(), new StarNetTool(exe), dir);
        Assert.Equal(StarRemovalProcessing.AlreadyStretched, r.Processing);
        Assert.Equal(3, r.Starless.Count);
        Assert.Equal(3, r.Stars.Count);
        var call = FakeStarNet.Calls(dir).Last();
        Assert.Contains("--unscreen", call);
        Assert.DoesNotContain("--linear", call);
    }

    [Fact]
    public void RejectsSmallImages()
    {
        var tool = new StarNetTool(FakeStarNet.Make(dir));
        var e = Assert.Throws<StarNetError>(() => StarRemoval.Run([Synth.Channel(400, 600)], Defaults(), tool, dir));
        Assert.Equal(StarNetError.ImageTooSmall(400, 600), e);
        Assert.Empty(FakeStarNet.Calls(dir));
        Assert.Equal(StarNetErrorKind.UnsupportedPlanes, Assert.Throws<StarNetError>(() => StarRemoval.Run([Synth.Channel(512, 512), Synth.Channel(512, 512)], Defaults(), tool, dir)).Kind);
    }

    [Fact]
    public void ProcessFileWritesBackgroundAndStarsFiles()
    {
        var exe = FakeStarNet.Make(dir);
        var src = Path.Combine(dir, "M1-Ha.fits");
        FitsWriter.Write([Synth.Channel(512, 512)], src, FitsWriter.Depth.Float32, [("FILTER", "Ha", null), ("EXPTIME", "300", null)]);
        var before = File.ReadAllBytes(src);
        var r = StarRemoval.ProcessFile(src, Defaults(), new StarNetTool(exe));
        Assert.Equal("M1-Ha_bg.fits", Path.GetFileName(r.Starless));
        Assert.Equal("M1-Ha_stars.fits", Path.GetFileName(r.Stars));
        var bg = ImageLoader.Load(r.Starless);
        Assert.Equal("Ha", bg.FilterName);   // acquisition keywords are carried over
        Assert.Equal(ChannelKind.Ha, bg.DetectedKind);
        Assert.Equal("Stars", FitsReader.Read(r.Stars).Header.String("IMAGETYP"));
        Assert.Equal(before, File.ReadAllBytes(src));   // the source is never modified
        // A TIFF source gives float TIFF outputs for linear data.
        var tif = Path.Combine(dir, "rgb.tif");
        var p = Synth.Channel(512, 512);
        RasterWriter.Write([p, p, p], tif, RasterFormat.Tiff16);
        var outDir = Path.Combine(dir, "out");
        var t = StarRemoval.ProcessFile(tif, Defaults(), new StarNetTool(exe), outDir);
        Assert.Equal(Path.Combine(outDir, "rgb_bg.tif"), t.Starless);
        var loaded = ImageLoader.Load(t.Starless);
        Assert.Equal(3, loaded.Planes.Count);
        Assert.Contains("32-bit float", loaded.FormatDescription);
    }

    [Fact]
    public void ProcessFilesReportsEachFileAndOverallProgress()
    {
        var exe = FakeStarNet.Make(dir);
        var a = Path.Combine(dir, "A-Ha.fits"); var b = Path.Combine(dir, "B-OIII.fits"); var small = Path.Combine(dir, "small.fits");
        FitsWriter.Write([Synth.Channel(512, 512)], a);
        FitsWriter.Write([Synth.Channel(512, 512, seed: 2)], b);
        FitsWriter.Write([Synth.Channel(300, 300)], small);
        var seen = new List<(int, double)>();
        var outcomes = StarRemoval.ProcessFiles([a, b, a, small], Defaults(), new StarNetTool(exe), progress: (i, f) => { lock (seen) seen.Add((i, f)); });
        Assert.Equal(3, outcomes.Count);   // the duplicate runs once
        Assert.Equal(Path.Combine(dir, "A-Ha_bg.fits"), outcomes[0].Result!.Starless);
        Assert.Equal(Path.Combine(dir, "B-OIII_stars.fits"), outcomes[1].Result!.Stars);
        Assert.Null(outcomes[2].Result);
        Assert.Contains("512", outcomes[2].Error);
        Assert.Equal(1.0, seen[^1].Item2);
        Assert.Contains((1, 0.5), seen);   // halfway through the second of three files
    }

    [Fact]
    public void CancelStopsTheProgram()
    {
        var tool = new StarNetTool(FakeStarNet.Make(dir, FakeStarNet.Behaviour.Slow));
        var cancel = new StarNetCancellation();
        _ = Task.Delay(500).ContinueWith(_ => cancel.Cancel());
        var t0 = Stopwatch.StartNew();
        var e = Assert.Throws<StarNetError>(() => StarRemoval.Run([Synth.Channel(512, 512)], Defaults(), tool, dir, cancellation: cancel));
        Assert.Equal(StarNetError.Cancelled(), e);
        Assert.True(t0.Elapsed.TotalSeconds < 10, $"{t0.Elapsed.TotalSeconds} s");
        // A cancelled token refuses to start another run.
        Assert.Throws<StarNetError>(() => tool.Run(new StarNetCommand(dir, Path.Combine(dir, "x")), cancellation: cancel));
        // A CancellationToken can drive it.
        using var cts = new CancellationTokenSource();
        var linked = new StarNetCancellation(cts.Token);
        cts.Cancel();
        Assert.True(linked.IsCancelled);
    }

    [Fact]
    public void FindsTheExecutableInsideAnUnpackedArchive()
    {
        var pkg = Path.Combine(dir, "starnet2_win_cli", "inner");
        Directory.CreateDirectory(Path.Combine(pkg, "StarNet2_weights.mlpackage"));
        var exe = Path.Combine(pkg, StarNet.ExecutableName);
        File.WriteAllText(exe, "");
        FakeStarNet.MakeExecutable(exe);
        File.WriteAllText(Path.Combine(pkg, "LICENSE.txt"), "terms");
        Assert.Equal(Path.GetFullPath(exe), Path.GetFullPath(StarNet.FindExecutable(dir)!));
        Assert.Equal("LICENSE.txt", Path.GetFileName(StarNet.LicenseFile(exe)));
        Assert.Null(StarNet.FindExecutable(Path.Combine(pkg, "StarNet2_weights.mlpackage")));
        // The official Windows installer layout: bin\starnet2.exe, the license one level up.
        var inst = Path.Combine(dir, "Program Files", "StarNet2");
        Directory.CreateDirectory(Path.Combine(inst, "bin"));
        var instExe = Path.Combine(inst, "bin", StarNet.ExecutableName);
        File.WriteAllText(instExe, "");
        File.WriteAllText(Path.Combine(inst, "LICENSE.txt"), "terms");
        Assert.Equal(Path.Combine(inst, "LICENSE.txt"), StarNet.LicenseFile(instExe));
    }

    // MARK: Setup (preferences, license, install from an archive)

    [Fact]
    public void SetupRemembersTheProgramAndTheLicense()
    {
        var prefsPath = Path.Combine(dir, "prefs", "starnet.json");
        var setup = new StarNetSetup(StarNetPreferences.JsonFile(prefsPath), Path.Combine(dir, "root"));
        Assert.Null(setup.RememberedExecutable);
        var fakeDir = Path.Combine(dir, "fake");
        var exe = FakeStarNet.Make(fakeDir);
        var found = setup.Choose(exe);
        Assert.Equal("2.6.2", found.Info.Version);
        Assert.Equal(Path.GetFullPath(exe), new StarNetSetup(StarNetPreferences.JsonFile(prefsPath)).RememberedExecutable);
        Assert.Equal(Path.GetFullPath(exe), setup.Candidates()[0]);
        Assert.Equal(found, setup.Locate());

        // No license file next to the program: the website's terms, accepted once.
        Assert.Null(found.ReadLicense());
        Assert.False(setup.IsLicenseAccepted(found));
        setup.AcceptLicense(found.ReadLicense());
        Assert.True(setup.IsLicenseAccepted(found));
        // Revised terms ask again.
        File.WriteAllText(Path.Combine(fakeDir, "LICENSE.txt"), "StarNet2 license v2");
        Assert.Equal("StarNet2 license v2", found.ReadLicense());
        Assert.False(setup.IsLicenseAccepted(found));
        setup.AcceptLicense(found.ReadLicense());
        Assert.True(setup.IsLicenseAccepted(found));
        Assert.Equal(64, StarNetSetup.LicenseFingerprint("x").Length);

        setup.Forget();
        Assert.Null(setup.RememberedExecutable);
        Assert.True(File.Exists(exe), "a program outside the install root is never deleted");
        Assert.Throws<StarNetError>(() => setup.Choose(Path.Combine(dir, "prefs")));   // a folder without starnet2
    }

    [Fact]
    public void SetupInstallsAnArchiveAfterCheckingItsChecksum()
    {
        // Build a package like the official one: a top folder holding the program and its license.
        var pkgSrc = Path.Combine(dir, "src", "starnet2_test_2.6.2_cli");
        // On Windows the stand-in is starnet2.cmd, not starnet2.exe: there the archive is refused (the full install is
        // exercised with the real package, see the README), elsewhere the shell script installs like the real program.
        FakeStarNet.Make(pkgSrc);
        File.WriteAllText(Path.Combine(pkgSrc, "LICENSE.txt"), "terms");
        var zip = Path.Combine(dir, "pkg.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(dir, "src"), zip);

        var root = Path.Combine(dir, "root");
        var setup = new StarNetSetup(StarNetPreferences.InMemory(), root);
        var bad = Assert.Throws<StarNetError>(() => setup.Install(zip, expectedSha256: new string('0', 64)));
        Assert.Contains("checksum", bad.Message);
        Assert.False(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any());

        if (OperatingSystem.IsWindows())
        {
            // No starnet2.exe in the archive: refused, and the staging folder is cleaned up.
            var none = Assert.Throws<StarNetError>(() => setup.Install(zip, StarNetSetup.Sha256(zip)));
            Assert.Contains("does not contain", none.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            return;
        }
        var installed = setup.Install(zip, StarNetSetup.Sha256(zip), "2.6.2");
        Assert.Equal(Path.Combine(root, "2.6.2", "starnet2"), installed.Executable);
        Assert.Equal("terms", installed.ReadLicense());
        Assert.Equal(installed.Executable, setup.RememberedExecutable);
        Assert.Equal(new[] { "2.6.2" }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
        setup.Forget();
        Assert.False(Directory.Exists(root));
    }
}
