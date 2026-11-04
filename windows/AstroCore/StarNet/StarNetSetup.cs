using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AstroCore;

/// <summary>A StarNet2 executable that answered <c>--machine-info</c>.</summary>
public sealed record StarNetInstallation(StarNetTool Tool, StarNetInfo Info)
{
    public string Executable => Tool.Executable;
    /// <summary>The license shipped with this executable (null: none found — the website's terms apply).</summary>
    public string? LicenseFile => StarNet.LicenseFile(Tool.Executable);
    public string? ReadLicense() => StarNetSetup.ReadLicense(Tool.Executable);
}

/// <summary>Bytes received so far and the expected total (0 when unknown).</summary>
public readonly record struct StarNetDownloadProgress(long Received, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
}

/// <summary>Where <see cref="StarNetSetup"/> remembers the chosen executable and the accepted license (two string keys,
/// <see cref="StarNetSetup.PathKey"/> and <see cref="StarNetSetup.LicenseKey"/>). The default is a small JSON file shared by
/// the app and the CLI; an app can route the keys to its own preferences instead.</summary>
public sealed class StarNetPreferences
{
    readonly Func<string, string?> get;
    readonly Action<string, string?> set;

    public StarNetPreferences(Func<string, string?> get, Action<string, string?> set) { this.get = get; this.set = set; }

    public string? Get(string key) => get(key);
    /// <summary>Stores a value; null removes the key.</summary>
    public void Set(string key, string? value) => set(key, value);

    /// <summary>Nothing persisted (tests, one-off runs).</summary>
    public static StarNetPreferences InMemory()
    {
        var d = new Dictionary<string, string>();
        return new StarNetPreferences(k => d.TryGetValue(k, out var v) ? v : null, (k, v) => { if (v == null) d.Remove(k); else d[k] = v; });
    }

    /// <summary>A JSON object file (read on every Get, rewritten on every Set; unreadable files count as empty).</summary>
    public static StarNetPreferences JsonFile(string path)
    {
        var gate = new object();
        JsonObject Load()
        {
            try { if (File.Exists(path)) return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            return [];
        }
        return new StarNetPreferences(
            k => { lock (gate) return Load()[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; },
            (k, v) =>
            {
                lock (gate)
                {
                    var o = Load();
                    if (v == null) o.Remove(k); else o[k] = v;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                        var tmp = path + ".tmp";
                        File.WriteAllText(tmp, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        File.Move(tmp, path, true);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            });
    }
}

/// <summary>
/// Finds, downloads and remembers the StarNet2 command-line program, and keeps track of the user's
/// acceptance of its license (StarNet2's terms require integrations to show the license and get an
/// explicit "I accept" before using it; downloads must come from the official site). No UI here: the app
/// shows <see cref="ReadLicense"/>'s text and calls <see cref="AcceptLicense"/>.
/// </summary>
public sealed class StarNetSetup
{
    public const string PathKey = "starnetExecutable";
    public const string LicenseKey = "starnetLicenseAccepted";

    /// <summary>%LOCALAPPDATA%\CombineColors (~/.local/share/CombineColors on Linux), where the app keeps its preferences.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CombineColors");
    /// <summary>Folder StarNet2 is installed into (no administrator rights needed): DefaultFolder\StarNet2\&lt;version&gt;.</summary>
    public static string DefaultInstallRoot => Path.Combine(DefaultFolder, "StarNet2");
    /// <summary>DefaultFolder\starnet.json: the remembered executable and license acceptance.</summary>
    public static string DefaultPreferencesPath => Path.Combine(DefaultFolder, "starnet.json");

    public string InstallRoot { get; }
    public StarNetPreferences Preferences { get; }

    public StarNetSetup(StarNetPreferences? preferences = null, string? installRoot = null)
    {
        Preferences = preferences ?? StarNetPreferences.JsonFile(DefaultPreferencesPath);
        InstallRoot = Path.GetFullPath(installRoot ?? DefaultInstallRoot);
    }

    // MARK: Finding the program

    public string? RememberedExecutable => Preferences.Get(PathKey);

    /// <summary>Executables worth trying, in order: the remembered path, the app's own install, the standard places, the PATH.</summary>
    public IReadOnlyList<string> Candidates()
    {
        var list = new List<string>();
        if (RememberedExecutable is string p) list.Add(p);
        if (StarNet.FindExecutable(InstallRoot) is string own) list.Add(own);
        list.AddRange(StarNet.StandardLocations);
        list.AddRange(StarNet.PathLocations());
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return list.Where(StarNet.IsExecutableFile).Select(Path.GetFullPath).Distinct(comparer).ToList();
    }

    /// <summary>The first candidate that is StarNet2 2.6+ (remembered for next time), or null. Runs each candidate's
    /// <c>--machine-info</c>: call it off the UI thread.</summary>
    public StarNetInstallation? Locate()
    {
        foreach (var exe in Candidates())
        {
            var tool = new StarNetTool(exe);
            try
            {
                var found = new StarNetInstallation(tool, tool.Info());
                Remember(found);
                return found;
            }
            catch (StarNetError) { }
        }
        return null;
    }

    /// <summary>Uses a file the user picked: the starnet2 program itself, the folder it is in, or a downloaded .zip
    /// archive (installed like a download, without checksum). Throws <see cref="StarNetError"/>.</summary>
    public StarNetInstallation Choose(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)) return Install(path);
        var exe = Directory.Exists(path) ? StarNet.FindExecutable(path) : path;
        if (exe == null) throw StarNetError.Install($"No program called “{StarNet.ExecutableName}” was found in {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}.");
        var tool = new StarNetTool(Path.GetFullPath(exe));
        var found = new StarNetInstallation(tool, tool.Info());
        Remember(found);
        return found;
    }

    public void Remember(StarNetInstallation installation) => Preferences.Set(PathKey, installation.Executable);

    /// <summary>Forgets the remembered program; the app's own install is removed too when that is what was remembered.</summary>
    public void Forget()
    {
        var remembered = RememberedExecutable;
        Preferences.Set(PathKey, null);
        if (remembered != null && IsInside(remembered, InstallRoot))
        {
            try { Directory.Delete(InstallRoot, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // MARK: License

    /// <summary>The license text shipped with an executable (null: no license file — the website's terms apply).</summary>
    public static string? ReadLicense(string executable)
    {
        if (StarNet.LicenseFile(executable) is not string f) return null;
        try { return File.ReadAllText(f, Encoding.UTF8); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>SHA-256 of the license text ("website" without one): acceptance is stored per text, so revised terms ask again.</summary>
    public static string LicenseFingerprint(string? licenseText) => licenseText == null ? "website" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(licenseText)));

    public bool IsLicenseAccepted(string? licenseText) => Preferences.Get(LicenseKey) == LicenseFingerprint(licenseText);
    public bool IsLicenseAccepted(StarNetInstallation installation) => IsLicenseAccepted(installation.ReadLicense());
    public void AcceptLicense(string? licenseText) => Preferences.Set(LicenseKey, LicenseFingerprint(licenseText));

    // MARK: Download

    static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CombineColors", null));
        return c;
    });

    /// <summary>Reads the official feed for this platform (null when offline or unreadable).</summary>
    public static async Task<StarNetRelease?> FetchLatestAsync(CancellationToken cancellationToken = default, HttpClient? http = null)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Get, StarNet.LatestFeed);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var resp = await (http ?? SharedHttp.Value).SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return StarNetRelease.ParseFeed(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { return null; }
    }

    public static bool IsUpdateAvailable(StarNetInfo installed, StarNetRelease latest) => StarNet.IsVersionNewer(latest.Version, installed.Version);

    /// <summary>Downloads a release archive to <paramref name="destination"/>. Throws <see cref="StarNetError"/> (Install) on a
    /// network or HTTP error, <see cref="OperationCanceledException"/> when cancelled (the partial file is removed).</summary>
    public static async Task DownloadAsync(StarNetRelease release, string destination, IProgress<StarNetDownloadProgress>? progress = null,
                                           CancellationToken cancellationToken = default, HttpClient? http = null)
    {
        try
        {
            using var resp = await (http ?? SharedHttp.Value).GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)resp.StatusCode != 200) throw StarNetError.Install($"The download did not work: the website answered with error {(int)resp.StatusCode}.");
            long total = resp.Content.Headers.ContentLength ?? release.SizeBytes ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            long received = 0, lastReport = -1;
            progress?.Report(new StarNetDownloadProgress(0, total));
            int n;
            while ((n = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                received += n;
                if (received - lastReport >= (1 << 20)) { lastReport = received; progress?.Report(new StarNetDownloadProgress(received, total)); }
            }
            progress?.Report(new StarNetDownloadProgress(received, total > 0 ? total : received));
        }
        catch (Exception e)
        {
            try { File.Delete(destination); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            if (e is StarNetError || e is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw StarNetError.Install($"The download did not work: {e.Message} Check the internet connection and try again, or download StarNet2 from its website yourself.");
        }
    }

    /// <summary>Downloads the portable package for this platform from the official site (the release given, else the
    /// feed's latest, else the built-in <see cref="StarNetRelease.Fallback"/>), checks its SHA-256 and unpacks it into
    /// <see cref="InstallRoot"/>. The result is remembered; the license still has to be accepted.</summary>
    public async Task<StarNetInstallation> DownloadAndInstallAsync(StarNetRelease? release = null, IProgress<StarNetDownloadProgress>? progress = null,
                                                                   CancellationToken cancellationToken = default)
    {
        release ??= await FetchLatestAsync(cancellationToken).ConfigureAwait(false) ?? StarNetRelease.Fallback();
        var archive = Path.Combine(Path.GetTempPath(), $"starnet2-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadAsync(release, archive, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => Install(archive, release.Sha256, release.Version), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(archive); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Unpacks an archive into <c>InstallRoot\&lt;version&gt;</c> (the package folder kept together: program, model,
    /// libraries, license), replacing an earlier install, and remembers it. Throws <see cref="StarNetError"/>.</summary>
    public StarNetInstallation Install(string archive, string? expectedSha256 = null, string? version = null)
    {
        if (expectedSha256 != null && !string.Equals(Sha256(archive), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw StarNetError.Install("The downloaded file is damaged (its checksum does not match). Please try again.");
        Directory.CreateDirectory(InstallRoot);
        var staging = Path.Combine(InstallRoot, $".unpacking-{Guid.NewGuid():N}");
        try
        {
            try { ZipFile.ExtractToDirectory(archive, staging); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw StarNetError.Install($"The archive could not be unpacked: {e.Message}");
            }
            if (!OperatingSystem.IsWindows()) MakeExecutable(staging);
            if (StarNet.FindExecutable(staging) is not string exe) throw StarNetError.Install("This archive does not contain the starnet2 program.");
            var info = new StarNetTool(exe).Info();
            var packageDir = Path.GetDirectoryName(exe)!;
            var dest = Path.Combine(InstallRoot, SafeName(version ?? info.Version));
            foreach (var old in Directory.EnumerateFileSystemEntries(InstallRoot).Where(e => !Path.GetFileName(e).StartsWith('.')).ToList())
            {
                try { if (Directory.Exists(old)) Directory.Delete(old, recursive: true); else File.Delete(old); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (Directory.Exists(dest)) dest += "-" + Guid.NewGuid().ToString("N")[..8];   // an old copy still in use
            Directory.Move(packageDir, dest);
            var installed = new StarNetInstallation(new StarNetTool(Path.Combine(dest, Path.GetFileName(exe))), info);
            Remember(installed);
            return installed;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return clean.Length == 0 ? "starnet2" : clean;
    }

    /// <summary>Zip extraction may drop the execute bit: give it back to the program.</summary>
    static void MakeExecutable(string folder)
    {
        foreach (var f in Directory.EnumerateFiles(folder, "starnet2", SearchOption.AllDirectories))
        {
            try { File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Lower-case hex SHA-256 of a file.</summary>
    public static string Sha256(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
