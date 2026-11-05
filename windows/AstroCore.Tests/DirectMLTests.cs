using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AstroCore;

namespace AstroCore.Tests;

public class DirectMLTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "CombineColorsTests", Environment.ProcessId.ToString(), "dml-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string exe, system;

    public DirectMLTests()
    {
        exe = Path.Combine(dir, "sn", "starnet2.exe");
        system = Path.Combine(dir, "System32");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        Directory.CreateDirectory(system);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(system, DirectML.FileName), [1]);
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static Version? Stub(string path) => File.ReadAllBytes(path) is [var b, ..] ? b switch { 1 => new Version(1, 8, 0), 2 => new Version(1, 15, 4), _ => null } : null;

    void Ort(bool directML) => File.WriteAllBytes(Path.Combine(dir, "sn", "onnxruntime.dll"), [.. "MZ....KERNEL32.dll"u8, .. (directML ? "DirectML.dll"u8 : "cudart64_12.dll"u8)]);

    [Fact]
    public void WindowsDirectMLIsTooOldForTheBundledRuntime()
    {
        Ort(directML: true);
        var st = DirectML.Check(exe, systemDirectory: system, versionOf: Stub);
        Assert.Equal(DirectMLState.Outdated, st.State);
        Assert.Equal(new Version(1, 8, 0), st.Found);
        Assert.False(st.IsLocalCopy(exe));
    }

    [Fact]
    public void ACopyNextToTheProgramWins()
    {
        Ort(directML: true);
        File.WriteAllBytes(Path.Combine(dir, "sn", DirectML.FileName), [2]);
        var st = DirectML.Check(exe, systemDirectory: system, versionOf: Stub);
        Assert.Equal(DirectMLState.UpToDate, st.State);
        Assert.True(st.IsLocalCopy(exe));
    }

    [Fact]
    public void BuildsWithoutDirectMLNeedNothing()
    {
        Assert.Equal(DirectMLState.NotNeeded, DirectML.Check(exe, systemDirectory: system, versionOf: Stub).State);   // no ONNX Runtime
        Ort(directML: false);
        Assert.Equal(DirectMLState.NotNeeded, DirectML.Check(exe, systemDirectory: system, versionOf: Stub).State);   // CUDA build
    }

    // A package like Microsoft.AI.DirectML: large files around the one needed.
    static (byte[] zip, byte[] dll) Package()
    {
        var rng = new Random(3);
        var dll = new byte[3_000_000]; rng.NextBytes(dll);
        var other = new byte[4_000_000]; rng.NextBytes(other);
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in new[] { ("bin/arm64-win/DirectML.dll", other), ("bin/x64-win/DirectML.dll", dll), ("bin/x86-win/DirectML.dll", other) })
            {
                using var s = z.CreateEntry(name, CompressionLevel.NoCompression).Open();
                s.Write(data);
            }
        }
        return (ms.ToArray(), dll);
    }

    sealed class Server(byte[] file, bool ranges) : HttpMessageHandler
    {
        public long Served;
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ranges && request.Headers.Range?.Ranges.FirstOrDefault() is { } r)
            {
                long from = r.From ?? 0, to = Math.Min(r.To ?? file.Length - 1, file.Length - 1);
                var part = file[(int)from..(int)(to + 1)];
                Served += part.Length;
                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(part) };
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
                return resp;
            }
            Served += file.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallPutsTheCheckedFileNextToTheProgram(bool ranges)
    {
        var (zip, dll) = Package();
        var server = new Server(zip, ranges);
        var package = new DirectMLPackage(new Uri("https://example.invalid/dml.nupkg"), "bin/x64-win/DirectML.dll", Convert.ToHexStringLower(SHA256.HashData(dll)), new Version(1, 15, 4), dll.Length);
        await DirectML.InstallAsync(exe, package: package, http: new HttpClient(server));
        Assert.Equal(dll, File.ReadAllBytes(Path.Combine(dir, "sn", DirectML.FileName)));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "sn"), "*DirectML*"));   // no temporary file left
        // With range requests only the file asked for (plus the zip directory) is fetched.
        if (ranges) Assert.True(server.Served < dll.Length + 2_100_000, $"served {server.Served}");
        else Assert.Equal(zip.Length, server.Served);
    }

    [Fact]
    public async Task ADamagedDownloadIsRefused()
    {
        var (zip, _) = Package();
        var package = new DirectMLPackage(new Uri("https://example.invalid/dml.nupkg"), "bin/x64-win/DirectML.dll", new string('0', 64), new Version(1, 15, 4), 0);
        var e = await Assert.ThrowsAsync<StarNetError>(() => DirectML.InstallAsync(exe, package: package, http: new HttpClient(new Server(zip, true))));
        Assert.Equal(StarNetErrorKind.Install, e.Kind);
        Assert.False(File.Exists(Path.Combine(dir, "sn", DirectML.FileName)));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "sn"), "*.download"));
    }
}
