using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AstroCore;

/// <summary>What the DirectML check found next to a StarNet2 program.</summary>
public enum DirectMLState
{
    /// <summary>Not Windows, or a StarNet2 build that does not use DirectML (CPU-only, CUDA runtime).</summary>
    NotNeeded,
    /// <summary>The DirectML StarNet2 loads (its own copy, else Windows') is recent enough.</summary>
    UpToDate,
    /// <summary>Only an older DirectML (Windows ships 1.8): the GPU fails and StarNet2 runs on the CPU.</summary>
    Outdated,
}

/// <param name="Found">Version of the DirectML.dll StarNet2 loads (null: none found).</param>
/// <param name="FoundPath">That file: the copy next to the program, else Windows' own.</param>
/// <param name="Available">The version <see cref="DirectML.InstallAsync"/> puts next to the program.</param>
public sealed record DirectMLStatus(DirectMLState State, Version? Found, string? FoundPath, Version Available)
{
    public bool IsLocalCopy(string executable) => FoundPath != null && string.Equals(Path.GetDirectoryName(FoundPath), Path.GetDirectoryName(Path.GetFullPath(executable)), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where the DirectML redistributable comes from: Microsoft's package on nuget.org and the file inside it.</summary>
public sealed record DirectMLPackage(Uri Url, string Entry, string Sha256, Version Version, long EntrySize)
{
    /// <summary>Microsoft.AI.DirectML 1.15.4, the version ONNX Runtime 1.24 (bundled with StarNet2 2.6) is built against.</summary>
    public static DirectMLPackage Default { get; } = new(
        new Uri("https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg"),
        "bin/x64-win/DirectML.dll",
        "9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1",
        new Version(1, 15, 4), 9_332_741);

    public static Uri LicenseUrl => new("https://www.nuget.org/packages/Microsoft.AI.DirectML/1.15.4/License");
}

/// <summary>
/// StarNet2's Windows package runs ONNX Runtime with DirectML but does not ship DirectML.dll, so Windows'
/// built-in copy is loaded — version 1.8 on Windows 10 / 11, too old for ONNX Runtime 1.24 (the warm-up fails
/// and StarNet2 falls back to the CPU). Putting Microsoft's redistributable DirectML.dll next to starnet2.exe
/// fixes it: Windows loads a DLL from the program's folder first. Nothing of StarNet2 is changed, and
/// deleting the file undoes it.
/// </summary>
public static class DirectML
{
    public const string FileName = "DirectML.dll";

    /// <summary>Which DirectML the StarNet2 program would load and whether it is recent enough.</summary>
    /// <param name="systemDirectory">Windows' System32 (tests pass another folder).</param>
    /// <param name="versionOf">Reads a DLL's file version (tests pass a stub).</param>
    public static DirectMLStatus Check(string executable, DirectMLPackage? package = null, string? systemDirectory = null, Func<string, Version?>? versionOf = null)
    {
        package ??= DirectMLPackage.Default;
        versionOf ??= FileVersion;
        var dir = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? ".";
        if ((!OperatingSystem.IsWindows() && systemDirectory == null) || !UsesDirectML(dir))
            return new DirectMLStatus(DirectMLState.NotNeeded, null, null, package.Version);
        string local = Path.Combine(dir, FileName);
        string system = Path.Combine(systemDirectory ?? Environment.SystemDirectory, FileName);
        string? found = File.Exists(local) ? local : File.Exists(system) ? system : null;
        var version = found == null ? null : versionOf(found);
        var state = version != null && version >= package.Version ? DirectMLState.UpToDate : DirectMLState.Outdated;
        return new DirectMLStatus(state, version, found, package.Version);
    }

    /// <summary>The ONNX Runtime next to the program imports DirectML.dll (the CUDA and CPU-only builds do not).</summary>
    public static bool UsesDirectML(string folder)
    {
        var ort = Path.Combine(folder, "onnxruntime.dll");
        if (!File.Exists(ort)) return false;
        try { return File.ReadAllBytes(ort).AsSpan().IndexOf("DirectML.dll"u8) >= 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static Version? FileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            if (v.FileMajorPart == 0 && v.FileMinorPart == 0 && v.FileBuildPart == 0) return null;
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException) { return null; }
    }

    /// <summary>
    /// Downloads DirectML.dll from Microsoft's package on nuget.org (only the x64 file, ≈ 9 MB, read out of the
    /// 200 MB package with HTTP range requests; the whole package when the server does not allow that), checks its
    /// SHA-256 and puts it next to the StarNet2 program. Throws <see cref="StarNetError"/> (Install), or
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static async Task InstallAsync(string executable, IProgress<StarNetDownloadProgress>? progress = null, CancellationToken cancellationToken = default,
                                          DirectMLPackage? package = null, HttpClient? http = null)
    {
        package ??= DirectMLPackage.Default;
        http ??= StarNetSetup.SharedHttp.Value;
        var dir = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? ".";
        var temp = Path.Combine(dir, $".DirectML-{Guid.NewGuid():N}.download");
        var dest = Path.Combine(dir, FileName);
        try
        {
            await Task.Run(() => Fetch(package, temp, http, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(StarNetSetup.Sha256(temp), package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw StarNetError.Install("The downloaded DirectML file is damaged (its checksum does not match). Please try again.");
            try { File.Move(temp, dest, overwrite: true); }
            catch (IOException) { throw StarNetError.Install("DirectML.dll could not be replaced: StarNet2 is still using it. Wait until it has finished and try again."); }
        }
        catch (UnauthorizedAccessException)
        {
            throw StarNetError.Install($"CombineColors may not write to the StarNet2 folder ({dir}). Use the StarNet2 copy CombineColors downloads, or copy {FileName} there yourself.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException && !cancellationToken.IsCancellationRequested)
        {
            throw StarNetError.Install($"The DirectML download did not work: {e.Message} Check the internet connection and try again.");
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static void Fetch(DirectMLPackage package, string destination, HttpClient http, IProgress<StarNetDownloadProgress>? progress, CancellationToken ct)
    {
        using var remote = HttpRangeStream.Open(http, package.Url, ct, out var whole);
        if (remote != null)
        {
            using var zip = new ZipArchive(remote, ZipArchiveMode.Read);
            Extract(zip, package, destination, progress, ct);
            return;
        }
        // No range requests: the whole package, then the file out of it.
        var archive = destination + ".nupkg";
        try
        {
            using (var resp = whole!)
            {
                long total = resp.Content.Headers.ContentLength ?? 0;
                using var src = resp.Content.ReadAsStream(ct);
                using var dst = File.Create(archive);
                Copy(src, dst, total, progress, ct);
            }
            using var zip = ZipFile.OpenRead(archive);
            Extract(zip, package, destination, null, ct);
        }
        finally { try { File.Delete(archive); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    static void Extract(ZipArchive zip, DirectMLPackage package, string destination, IProgress<StarNetDownloadProgress>? progress, CancellationToken ct)
    {
        var entry = zip.GetEntry(package.Entry) ?? throw StarNetError.Install($"The DirectML package does not contain {package.Entry}.");
        using var src = entry.Open();
        using var dst = File.Create(destination);
        Copy(src, dst, entry.Length, progress, ct);
    }

    static void Copy(Stream src, Stream dst, long total, IProgress<StarNetDownloadProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 18];
        long done = 0, last = -1;
        progress?.Report(new StarNetDownloadProgress(0, total));
        int n;
        while ((n = src.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buffer, 0, n);
            done += n;
            if (done - last >= 1 << 20) { last = done; progress?.Report(new StarNetDownloadProgress(done, total)); }
        }
        progress?.Report(new StarNetDownloadProgress(done, total > 0 ? total : done));
    }
}

/// <summary>A read-only, seekable view of a remote file through HTTP range requests (1 MB blocks), so
/// <see cref="ZipArchive"/> reads only the central directory and the one entry it is asked for.</summary>
sealed class HttpRangeStream : Stream
{
    const int BlockSize = 1 << 20;
    readonly HttpClient http;
    readonly Uri url;
    readonly CancellationToken ct;
    readonly long length;
    long position, blockStart = -1;
    readonly byte[] block = new byte[BlockSize];
    int blockLength;

    HttpRangeStream(HttpClient http, Uri url, long length, CancellationToken ct) { this.http = http; this.url = url; this.length = length; this.ct = ct; }

    /// <summary>Null when the server ignores range requests: <paramref name="whole"/> is then its answer with the whole file (to be read and disposed).</summary>
    public static HttpRangeStream? Open(HttpClient http, Uri url, CancellationToken ct, out HttpResponseMessage? whole)
    {
        whole = null;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.Length is long len) { resp.Dispose(); return new HttpRangeStream(http, url, len, ct); }
        if (!resp.IsSuccessStatusCode)
        {
            int code = (int)resp.StatusCode;
            resp.Dispose();
            throw StarNetError.Install($"The DirectML download did not work: nuget.org answered with error {code}.");
        }
        whole = resp;
        return null;
    }

    void Load(long start)
    {
        int count = (int)Math.Min(BlockSize, length - start);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(start, start + count - 1);
        using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode != HttpStatusCode.PartialContent) throw new IOException($"the server answered {(int)resp.StatusCode} to a range request.");
        using var s = resp.Content.ReadAsStream(ct);
        int got = 0, n;
        while (got < count && (n = s.Read(block, got, count - got)) > 0) got += n;
        if (got < count) throw new IOException("the download was cut short.");
        blockStart = start; blockLength = count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (position >= length || buffer.Length == 0) return 0;
        if (blockStart < 0 || position < blockStart || position >= blockStart + blockLength) Load(position / BlockSize * BlockSize);
        int at = (int)(position - blockStart);
        int n = Math.Min(buffer.Length, blockLength - at);
        block.AsSpan(at, n).CopyTo(buffer);
        position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => length + offset };
        if (position < 0) throw new IOException("seek before the start");
        return position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
