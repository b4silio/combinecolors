using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AstroCore;

/// <summary>
/// StarNet2 (https://starnetastro.com, by Mikita Misiura) is a separate, free command-line program
/// that removes stars. CombineColors never bundles it: the user downloads it from the official
/// site (the app can do that for them) and accepts its license; the app then runs the executable
/// as a separate process, which is the integration its license allows.
/// </summary>
public static class StarNet
{
    public static readonly Uri Website = new("https://starnetastro.com/cli-tools/starnet/");
    /// <summary>Official machine-readable feed of the latest CLI releases (direct archive links + SHA-256).</summary>
    public static readonly Uri LatestFeed = new("https://starnetastro.com/cli-tools/latest.json");
    /// <summary>Name of the executable in every StarNet2 CLI package of this platform ("starnet2.exe" on Windows).</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "starnet2.exe" : "starnet2";
    /// <summary>StarNet2 refuses smaller images (256 with <c>--upsample</c>).</summary>
    public const int MinimumSize = 512;

    /// <summary>Platform key of the feed for this machine: windows-x64, linux-x64, macos-arm64 or macos-x64.
    /// (Windows on ARM runs the x64 build through emulation; there is no Linux arm64 build.)</summary>
    public static string PlatformKey
    {
        get
        {
            if (OperatingSystem.IsWindows()) return "windows-x64";
            if (OperatingSystem.IsMacOS()) return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "macos-arm64" : "macos-x64";
            return "linux-x64";
        }
    }

    /// <summary>Places where a StarNet2 installed by its own installer (or by hand, following its README) lives.</summary>
    public static IReadOnlyList<string> StandardLocations
    {
        get
        {
            var list = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                // The official setup puts starnet2.exe under C:\Program Files\StarNet2\bin (model under lib\starnet2).
                foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    list.Add(Path.Combine(root, "StarNet2", "bin", ExecutableName));
                    list.Add(Path.Combine(root, "StarNet2", ExecutableName));
                }
            }
            else
            {
                list.Add("/usr/local/bin/starnet2");
                if (OperatingSystem.IsMacOS()) list.Add("/opt/homebrew/bin/starnet2");
                else
                {
                    list.Add("/usr/bin/starnet2");
                    list.Add("/opt/starnet2/bin/starnet2");
                    list.Add("/opt/StarNet2/bin/starnet2");
                }
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home)) list.Add(Path.Combine(home, ".local", "bin", "starnet2"));
            }
            return list;
        }
    }

    /// <summary>Every <see cref="ExecutableName"/> found in the folders of the PATH variable, in order.</summary>
    public static IReadOnlyList<string> PathLocations()
    {
        var list = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return list;
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(raw.Trim('"'), ExecutableName);
                if (IsExecutableFile(candidate)) list.Add(Path.GetFullPath(candidate));
            }
            catch (ArgumentException) { }
        }
        return list;
    }

    /// <summary>License file to show for an executable: the one shipped next to it (portable archive), or the one
    /// an installer puts beside the bin folder or under share/doc.</summary>
    public static string? LicenseFile(string executable)
    {
        var dir = Path.GetDirectoryName(ResolveLinks(executable));
        if (dir == null) return null;
        var parent = Path.GetDirectoryName(dir);
        var candidates = new List<string> { Path.Combine(dir, "LICENSE.txt") };
        if (parent != null)
        {
            candidates.Add(Path.Combine(parent, "share", "doc", "starnet2", "LICENSE.txt"));
            candidates.Add(Path.Combine(parent, "LICENSE.txt"));
            candidates.Add(Path.Combine(parent, "doc", "LICENSE.txt"));
        }
        if (!OperatingSystem.IsWindows()) candidates.Add("/usr/local/share/doc/starnet2/LICENSE.txt");
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Finds the starnet2 executable inside a folder (an unpacked archive), a few levels deep.</summary>
    public static string? FindExecutable(string folder, int depth = 3)
    {
        var direct = Path.Combine(folder, ExecutableName);
        if (IsExecutableFile(direct)) return direct;
        if (depth <= 0 || !Directory.Exists(folder)) return null;
        string[] dirs;
        try { dirs = Directory.GetDirectories(folder); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var sub in dirs.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name.EndsWith(".mlpackage", StringComparison.OrdinalIgnoreCase)) continue;
            try { if ((File.GetAttributes(sub) & FileAttributes.Hidden) != 0) continue; } catch (IOException) { continue; }
            if (FindExecutable(sub, depth - 1) is string found) return found;
        }
        return null;
    }

    /// <summary>A regular file that can be run (on Windows any existing file; elsewhere one with an execute bit).</summary>
    public static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            const UnixFileMode exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & exec) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    static string ResolveLinks(string path)
    {
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }

    /// <summary>"2.6.10" is newer than "2.6.2".</summary>
    public static bool IsVersionNewer(string a, string b)
    {
        static int[] Parts(string s) => s.Split('.').Select(t => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
        var x = Parts(a); var y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p > q;
        }
        return false;
    }
}

/// <summary>What <c>starnet2 --machine-info</c> reports.</summary>
/// <param name="Backend">Inference backend ("CoreML" on Apple Silicon, "ONNX Runtime" elsewhere).</param>
public sealed record StarNetInfo(string Version, string Build, string Backend)
{
    /// <summary>Parses the <c>--machine-info</c> JSON; null when it is not StarNet2's.</summary>
    public static StarNetInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            if (Str(o, "product")?.ToLowerInvariant() != "starnet2" || Str(o, "version") is not string version) return null;
            var backend = o.TryGetProperty("backend", out var b) && b.ValueKind == JsonValueKind.Object ? Str(b, "display_name") ?? "" : "";
            return new StarNetInfo(version, Str(o, "build") ?? "", backend);
        }
        catch (JsonException) { return null; }
    }

    public static StarNetInfo? Parse(byte[] utf8) => Parse(Encoding.UTF8.GetString(utf8));

    internal static string? Str(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>One release package from the official feed.</summary>
public sealed record StarNetRelease(string Version, Uri Url, string? Sha256, long? SizeBytes)
{
    public string FileName => Path.GetFileName(Url.AbsolutePath);

    /// <summary>The portable archive for a platform key (default: this machine's), from the <c>latest.json</c> feed.
    /// Only https links are taken.</summary>
    public static StarNetRelease? ParseFeed(string json, string? platform = null)
    {
        platform ??= StarNet.PlatformKey;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryObj(doc.RootElement, "tools", out var tools) || !TryObj(tools, "starnet2", out var tool) || !TryObj(tool, "latest", out var latest)
                || !TryObj(latest, platform, out var plat) || StarNetInfo.Str(plat, "version") is not string version
                || !TryObj(plat, "packages", out var packages) || !TryObj(packages, "archive", out var archive)
                || StarNetInfo.Str(archive, "url") is not string s || !Uri.TryCreate(s, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                return null;
            long? size = archive.TryGetProperty("size_bytes", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var n) ? n : null;
            return new StarNetRelease(version, url, StarNetInfo.Str(archive, "sha256"), size);
        }
        catch (JsonException) { return null; }
    }

    static bool TryObj(JsonElement e, string key, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;
    }

    /// <summary>Used when the feed cannot be read (release of 2026-09-19).</summary>
    public static StarNetRelease Fallback(string? platform = null) => (platform ?? StarNet.PlatformKey) switch
    {
        "windows-x64" => new("2.6.2", new Uri("https://download.starnetastro.com/starnet2_win_2.6.2-0242_ORT_x64_cli.zip"),
                             "fbd5c7bdbbbee0cdbabf101b2c7ac2af3d33e3c477f9585f38f98b882f1551a0", 192_822_891),
        "macos-arm64" => new("2.6.2", new Uri("https://download.starnetastro.com/starnet2_macos-arm64_2.6.2-0242_COREML_arm64_cli.zip"),
                             "dfeb471e983a9de1233b439a8c56d3e373ed26e37c85aababe737187d164d20f", 127_977_753),
        "macos-x64" => new("2.6.2", new Uri("https://download.starnetastro.com/starnet2_macos-x64_2.6.2-0242_ORT_x64_cli.zip"),
                           "e0af667ae9d1a0e6d869d5fdef1738aa2c9077dc62bcbada706a88d9cc6a524a", 151_943_354),
        _ => new("2.6.2", new Uri("https://download.starnetastro.com/starnet2_linux_2.6.2-0242_ORT_x64_cli.zip"),
                 "066a7683f0321ae8400d3bc1023a48747ac228e1d14e76b5d39c4e48cbce4920", 151_481_786),
    };
}

public enum StarNetErrorKind { NotStarNet2, ImageTooSmall, UnsupportedPlanes, Failed, MissingOutput, Cancelled, Install }

/// <summary>Everything that can go wrong finding, installing or running StarNet2 (the Swift <c>StarNetError</c> cases,
/// plus <see cref="StarNetErrorKind.Install"/> for download / unpacking problems). Two errors are equal when their
/// kind and details are.</summary>
public sealed class StarNetError : Exception, IEquatable<StarNetError>
{
    public StarNetErrorKind Kind { get; }
    /// <summary>File / program name (NotStarNet2, MissingOutput), StarNet2's last messages (Failed) or the install problem (Install).</summary>
    public string Detail { get; }
    /// <summary>Exit code for Failed; width × height for ImageTooSmall; plane count for UnsupportedPlanes.</summary>
    public int Status { get; }
    public int Width { get; }
    public int Height { get; }

    StarNetError(StarNetErrorKind kind, string message, string detail = "", int status = 0, int width = 0, int height = 0) : base(message)
    {
        Kind = kind; Detail = detail; Status = status; Width = width; Height = height;
    }

    public static StarNetError NotStarNet2(string name) => new(StarNetErrorKind.NotStarNet2, $"{name} is not StarNet2 version 2.6 or newer.", name);
    public static StarNetError ImageTooSmall(int w, int h) => new(StarNetErrorKind.ImageTooSmall,
        $"StarNet2 needs images of at least {StarNet.MinimumSize} × {StarNet.MinimumSize} pixels (this one is {w} × {h}).", width: w, height: h);
    public static StarNetError UnsupportedPlanes(int n) => new(StarNetErrorKind.UnsupportedPlanes, $"StarNet2 takes grey or RGB images (this one has {n} planes).", status: n);
    public static StarNetError Failed(int status, string message) => new(StarNetErrorKind.Failed,
        $"StarNet2 stopped with an error (code {status})." + (message.Length == 0 ? "" : "\n" + message), message, status);
    public static StarNetError MissingOutput(string file) => new(StarNetErrorKind.MissingOutput, $"StarNet2 did not write {file}.", file);
    public static StarNetError Cancelled() => new(StarNetErrorKind.Cancelled, "Star removal was stopped.");
    public static StarNetError Install(string message) => new(StarNetErrorKind.Install, message, message);

    public bool Equals(StarNetError? o) => o != null && Kind == o.Kind && Detail == o.Detail && Status == o.Status && Width == o.Width && Height == o.Height;
    public override bool Equals(object? obj) => Equals(obj as StarNetError);
    public override int GetHashCode() => HashCode.Combine(Kind, Detail, Status, Width, Height);
}

/// <summary>Stops a running StarNet2 process (and its child processes) from another thread.</summary>
public sealed class StarNetCancellation
{
    readonly object gate = new();
    Process? process;
    bool cancelled;

    public StarNetCancellation() { }

    /// <summary>Cancels when the token does.</summary>
    public StarNetCancellation(CancellationToken token) { if (token.CanBeCanceled) token.Register(Cancel); }

    public bool IsCancelled { get { lock (gate) return cancelled; } }

    public void Cancel()
    {
        Process? p;
        lock (gate) { cancelled = true; p = process; }
        if (p != null) Kill(p);
    }

    internal static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    /// <summary>Registers the running process; false when the job was cancelled already.</summary>
    internal bool Attach(Process p)
    {
        lock (gate)
        {
            if (cancelled) return false;
            process = p;
            return true;
        }
    }

    internal void Detach() { lock (gate) process = null; }
}

/// <summary>The arguments of one StarNet2 run.</summary>
public sealed record StarNetCommand(string Input, string Output)
{
    /// <summary>Difference star layer (<c>--mask</c>): original − starless, the natural layer for linear data.</summary>
    public string? Mask { get; set; }
    /// <summary>Unscreen star layer (<c>--unscreen</c>): Screen it on the starless image to get the original back.</summary>
    public string? Unscreen { get; set; }
    /// <summary><c>--linear</c>: StarNet2 stretches the data itself (per-channel MTF) and undoes the stretch.</summary>
    public bool Linear { get; set; }
    public float? ShadowsClipping { get; set; }
    public float? TargetBackground { get; set; }
    public bool Upsample { get; set; }

    public IReadOnlyList<string> Arguments
    {
        get
        {
            var a = new List<string> { "--machine-progress", "-i", Input, "-o", Output };
            if (Mask != null) { a.Add("--mask"); a.Add(Mask); }
            if (Unscreen != null) { a.Add("--unscreen"); a.Add(Unscreen); }
            if (Linear)
            {
                a.Add("--linear");
                // StarNet2 wants a negative clipping factor and a target strictly inside (0, 1).
                if (ShadowsClipping is float s) { a.Add("--shadows-clipping"); a.Add(MathF.Min(s, -0.01f).ToString("F3", CultureInfo.InvariantCulture)); }
                if (TargetBackground is float t) { a.Add("--target-background"); a.Add(Math.Clamp(t, 0.01f, 0.99f).ToString("F3", CultureInfo.InvariantCulture)); }
            }
            if (Upsample) a.Add("--upsample");
            return a;
        }
    }
}

/// <summary>A StarNet2 executable.</summary>
public sealed record StarNetTool(string Executable)
{
    /// <summary>Asks the executable for its version (<c>--machine-info</c>); throws <see cref="StarNetError"/> (NotStarNet2)
    /// when it is not StarNet2 2.6+.</summary>
    public StarNetInfo Info(TimeSpan? timeout = null)
    {
        var name = Path.GetFileName(Executable);
        var psi = StartInfo(["--machine-info"], workingDirectory: null);
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, _) => { };
        try { p.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { throw StarNetError.NotStarNet2(name); }
        try { p.StandardInput.Close(); } catch (IOException) { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeout ?? TimeSpan.FromSeconds(30))) StarNetCancellation.Kill(p);
        p.WaitForExit();
        string text;
        lock (output) text = output.ToString();
        if (p.ExitCode != 0 || StarNetInfo.Parse(text) is not StarNetInfo info) throw StarNetError.NotStarNet2(name);
        return info;
    }

    /// <summary>Runs one command, reporting progress in [0, 1] from StarNet2's JSON-lines progress events (called on
    /// a worker thread). Throws <see cref="StarNetError"/>: Failed with StarNet2's last messages, or Cancelled.</summary>
    public void Run(StarNetCommand command, Action<double>? progress = null, StarNetCancellation? cancellation = null)
    {
        if (cancellation?.IsCancelled == true) throw StarNetError.Cancelled();
        var psi = StartInfo(command.Arguments, Path.GetDirectoryName(Path.GetFullPath(command.Output)));
        using var p = new Process { StartInfo = psi };
        var messages = new List<string>();
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            switch (StarNetEvent.Parse(e.Data))
            {
                case StarNetEvent.Progress pr: progress?.Invoke(pr.Fraction); break;
                case StarNetEvent.Message m: lock (messages) messages.Add(m.Text); break;
                case StarNetEvent.Other: break;
                case null: lock (messages) messages.Add(e.Data.Trim()); break;
            }
        };
        try { p.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { throw StarNetError.Failed(-1, e.Message); }
        try { p.StandardInput.Close(); } catch (IOException) { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (cancellation != null && !cancellation.Attach(p))
        {
            StarNetCancellation.Kill(p);
            p.WaitForExit();
            throw StarNetError.Cancelled();
        }
        try { p.WaitForExit(); }
        finally { cancellation?.Detach(); }
        if (cancellation?.IsCancelled == true) throw StarNetError.Cancelled();
        if (p.ExitCode != 0)
        {
            string tail;
            lock (messages) tail = string.Join("\n", messages.TakeLast(6));
            throw StarNetError.Failed(p.ExitCode, tail);
        }
    }

    ProcessStartInfo StartInfo(IEnumerable<string> arguments, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        // ArgumentList quotes every argument as needed (paths with spaces).
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>One line of StarNet2's machine output on stderr.</summary>
internal abstract record StarNetEvent
{
    internal sealed record Progress(double Fraction) : StarNetEvent;
    /// <summary>A warning or an error worth showing when the run fails.</summary>
    internal sealed record Message(string Text) : StarNetEvent;
    internal sealed record Other : StarNetEvent;

    /// <summary>Null when the line is not one of StarNet2's JSON events.</summary>
    internal static StarNetEvent? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || StarNetInfo.Str(o, "schema") is not string schema) return null;
            if (schema.StartsWith("starnetastro.cli.progress", StringComparison.Ordinal))
            {
                if (StarNetInfo.Str(o, "event") == "error") return new Message(StarNetInfo.Str(o, "message") ?? "error");
                if (o.TryGetProperty("percent", out var pct) && pct.ValueKind == JsonValueKind.Number) return new Progress(Math.Clamp(pct.GetDouble() / 100, 0, 1));
                return new Other();
            }
            if (schema.StartsWith("starnetastro.cli.diagnostic", StringComparison.Ordinal) && StarNetInfo.Str(o, "severity") is string sev && sev != "info")
                return new Message(StarNetInfo.Str(o, "message") ?? sev);
            return new Other();
        }
        catch (JsonException) { return null; }
    }
}
