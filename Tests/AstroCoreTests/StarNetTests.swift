import XCTest
@testable import AstroCore

/// A stand-in for the StarNet2 program (the real one is never run by the tests): a shell script
/// that answers `--machine-info`, logs its arguments, emits progress lines and copies the input to
/// every requested output.
enum FakeStarNet {
    enum Behaviour { case copy, fail, slow }

    static func make(in dir: URL, behaviour: Behaviour = .copy) throws -> URL {
        let exe = dir.appendingPathComponent("starnet2")
        let before: String
        switch behaviour {
        case .copy: before = ""
        case .fail: before = #"echo '{"schema":"starnetastro.cli.progress.v1","event":"error","message":"model could not be loaded"}' >&2; exit 3"#
        case .slow: before = "sleep 20"
        }
        let script = #"""
        #!/bin/bash
        echo "$@" >> "$(dirname "$0")/calls.log"
        if [ "$1" = "--machine-info" ]; then
          echo '{"schema":"starnetastro.cli.machine-info.v1","product":"starnet2","version":"2.6.2","build":"0242","backend":{"id":"coreml","display_name":"CoreML"}}'
          exit 0
        fi
        in=""; out=""; mask=""; uns=""
        while [ $# -gt 0 ]; do
          case "$1" in
            -i) in="$2"; shift;;
            -o) out="$2"; shift;;
            --mask) mask="$2"; shift;;
            --unscreen) uns="$2"; shift;;
          esac
          shift
        done
        """# + "\n" + before + "\n" + #"""
        echo '{"schema":"starnetastro.cli.diagnostic.v1","severity":"info","code":"input","message":"Reading"}' >&2
        echo '{"schema":"starnetastro.cli.progress.v1","event":"progress","stage":"inference","current":1,"total":2,"percent":50.0}' >&2
        cp "$in" "$out"
        [ -n "$mask" ] && cp "$in" "$mask"
        [ -n "$uns" ] && cp "$in" "$uns"
        echo '{"schema":"starnetastro.cli.progress.v1","event":"finish","stage":"inference","current":2,"total":2,"percent":100.0,"ok":true}' >&2
        exit 0
        """#
        try script.write(to: exe, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: exe.path)
        return exe
    }

    static func calls(in dir: URL) -> [String] {
        ((try? String(contentsOf: dir.appendingPathComponent("calls.log"), encoding: .utf8)) ?? "").split(separator: "\n").map(String.init)
    }
}

final class StarNetTests: XCTestCase {
    var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("StarNetTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: dir) }

    // MARK: Pure parts

    func testCommandArguments() {
        var c = StarNetCommand(input: URL(fileURLWithPath: "/a b/in.fits"), output: URL(fileURLWithPath: "/o/out.fits"))
        XCTAssertEqual(c.arguments, ["--machine-progress", "-i", "/a b/in.fits", "-o", "/o/out.fits"])
        c.mask = URL(fileURLWithPath: "/o/stars.fits")
        c.linear = true
        c.shadowsClipping = 0.5          // StarNet2 rejects a positive factor
        c.targetBackground = 0.2
        c.upsample = true
        XCTAssertEqual(c.arguments, ["--machine-progress", "-i", "/a b/in.fits", "-o", "/o/out.fits", "--mask", "/o/stars.fits",
                                     "--linear", "--shadows-clipping", "-0.010", "--target-background", "0.200", "--upsample"])
        // Linear-only options are left out without --linear.
        c.linear = false
        XCTAssertFalse(c.arguments.contains("--target-background"))
    }

    func testMachineInfoAndFeedParsing() throws {
        let info = StarNetInfo.parse(Data(#"{"product":"starnet2","version":"2.6.2","build":"0242","backend":{"display_name":"CoreML"}}"#.utf8))
        XCTAssertEqual(info, StarNetInfo(version: "2.6.2", build: "0242", backend: "CoreML"))
        XCTAssertNil(StarNetInfo.parse(Data(#"{"product":"deepsnr","version":"1.3.2"}"#.utf8)))
        XCTAssertNil(StarNetInfo.parse(Data("starnet2 version 2.6.2".utf8)))

        let feed = #"""
        {"schema":"starnetastro.cli-tools.latest.v1","tools":{"starnet2":{"latest":{
          "macos-arm64":{"version":"2.7.0","packages":{"archive":{"url":"https://download.starnetastro.com/a.zip","sha256":"abc","size_bytes":123}}},
          "macos-x64":{"version":"2.7.0","packages":{"archive":{"url":"http://insecure.example/b.zip"}}}}}}}
        """#
        let r = StarNetRelease.parseFeed(Data(feed.utf8), platform: "macos-arm64")
        XCTAssertEqual(r, StarNetRelease(version: "2.7.0", url: URL(string: "https://download.starnetastro.com/a.zip")!, sha256: "abc", sizeBytes: 123))
        XCTAssertNil(StarNetRelease.parseFeed(Data(feed.utf8), platform: "macos-x64"), "only https links are taken")
        XCTAssertNil(StarNetRelease.parseFeed(Data("{}".utf8)))
        XCTAssertTrue(StarNetRelease.fallback(platform: "macos-arm64").url.absoluteString.contains("COREML_arm64"))

        XCTAssertTrue(StarNet.isVersion("2.6.10", newerThan: "2.6.2"))
        XCTAssertTrue(StarNet.isVersion("3", newerThan: "2.9.9"))
        XCTAssertFalse(StarNet.isVersion("2.6.2", newerThan: "2.6.2"))
        XCTAssertFalse(StarNet.isVersion("2.6", newerThan: "2.6.0"))
    }

    func testProgressEvents() {
        XCTAssertEqual(StarNetEvent.parse(Data(#"{"schema":"starnetastro.cli.progress.v1","event":"progress","percent":42.5}"#.utf8)), .progress(0.425))
        XCTAssertEqual(StarNetEvent.parse(Data(#"{"schema":"starnetastro.cli.progress.v1","event":"error","message":"boom"}"#.utf8)), .message("boom"))
        XCTAssertEqual(StarNetEvent.parse(Data(#"{"schema":"starnetastro.cli.diagnostic.v1","severity":"warning","message":"clipped"}"#.utf8)), .message("clipped"))
        XCTAssertEqual(StarNetEvent.parse(Data(#"{"schema":"starnetastro.cli.diagnostic.v1","severity":"info","message":"x"}"#.utf8)), .other)
        XCTAssertNil(StarNetEvent.parse(Data("12 %".utf8)))
    }

    func testOutputNames() {
        let fits = StarRemoval.outputURLs(for: URL(fileURLWithPath: "/data/M42-Ha.fit"))
        XCTAssertEqual(fits.starless.path, "/data/M42-Ha_bg.fits")
        XCTAssertEqual(fits.stars.path, "/data/M42-Ha_stars.fits")
        XCTAssertEqual(StarRemoval.outputURLs(for: URL(fileURLWithPath: "/data/Stack_Ha.xisf")).starless.lastPathComponent, "Stack_Ha_bg.fits")
        let tif = StarRemoval.outputURLs(for: URL(fileURLWithPath: "/data/rgb.png"), folder: URL(fileURLWithPath: "/out", isDirectory: true))
        XCTAssertEqual(tif.starless.path, "/out/rgb_bg.tif")
        XCTAssertEqual(tif.stars.path, "/out/rgb_stars.tif")
        XCTAssertTrue(StarRemoval.isStarless(URL(fileURLWithPath: "/data/M42-Ha_bg.fits")))
        XCTAssertFalse(StarRemoval.isStarless(URL(fileURLWithPath: "/data/M42-Ha.fits")))
    }

    func testInverseStretchRoundTripsEveryMethod() {
        let stats = Statistics.compute(image: Synth.channel())
        for method in StretchMethod.allCases {
            var s = StretchSettings()
            s.method = method
            if method == .ghs { s.ghsSPPercentile = 30 }
            s.gamma = method == .log ? 0.8 : 1
            let r = Stretch.resolve(s, stats: stats)
            let inv = InverseStretch(r)
            let lo = method == .none ? 0 : r.c0, hi = method == .none ? 1 : r.c1
            // The inverse is only defined where the stretch is not clipped (between the black and white points).
            let a = lo, b = hi
            var worst: Float = 0
            for i in 1..<200 {
                let x = a + (b - a) * Float(i) / 200
                let back = inv.evaluate(r.evaluate(x))
                worst = max(worst, abs(back - x) / max(hi - lo, 1e-6))
            }
            XCTAssertLessThan(worst, 2e-3, "\(method)")
            // Background-level values come back much closer (the stretch is steep there).
            let bg = stats.median
            if bg > lo && bg < hi && method != .mtf { XCTAssertEqual(inv.evaluate(r.evaluate(bg)), bg, accuracy: 2e-5 * max(hi - lo, 1e-3), "\(method)") }
        }
    }

    // MARK: Runs with the stand-in program

    func testToolInfoAndFailures() throws {
        let tool = StarNetTool(executable: try FakeStarNet.make(in: dir))
        XCTAssertEqual(try tool.info().version, "2.6.2")
        let notStarNet = dir.appendingPathComponent("other")
        try "#!/bin/bash\necho hello\n".write(to: notStarNet, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: notStarNet.path)
        XCTAssertThrowsError(try StarNetTool(executable: notStarNet).info())
        XCTAssertThrowsError(try StarNetTool(executable: dir.appendingPathComponent("missing")).info())

        let failDir = dir.appendingPathComponent("fail"); try FileManager.default.createDirectory(at: failDir, withIntermediateDirectories: true)
        let failing = StarNetTool(executable: try FakeStarNet.make(in: failDir, behaviour: .fail))
        XCTAssertThrowsError(try StarRemoval.run(planes: [Synth.channel(width: 512, height: 512)], options: StarRemovalOptions(), tool: failing, workDirectory: dir)) { e in
            guard case StarNetError.failed(let status, let message) = e else { return XCTFail("\(e)") }
            XCTAssertEqual(status, 3)
            XCTAssertTrue(message.contains("model could not be loaded"))
        }
    }

    func testLinearRunUsesStarNetLinearModeWithProjectValues() throws {
        let exe = try FakeStarNet.make(in: dir)
        let plane = Synth.channel(width: 512, height: 520)
        var opts = StarRemovalOptions()
        opts.stretch.targetBackground = 0.2
        opts.stretch.shadowsClipping = -2.8
        var progress: [Double] = []
        let r = try StarRemoval.run(planes: [plane], options: opts, tool: StarNetTool(executable: exe), workDirectory: dir, progress: { progress.append($0) })
        XCTAssertEqual(r.processing, .starnetLinear)
        XCTAssertEqual(r.starless.count, 1)
        XCTAssertEqual(r.starless[0].toArray(), plane.toArray(), "the stand-in copies its input: the data reach StarNet2 unscaled")
        let call = try XCTUnwrap(FakeStarNet.calls(in: dir).last)
        XCTAssertTrue(call.contains("--linear --shadows-clipping -2.800 --target-background 0.200"), call)
        XCTAssertTrue(call.contains("--mask"))
        XCTAssertFalse(call.contains("--unscreen"))
        XCTAssertEqual(progress, [0.5, 1])
    }

    func testFloatADUDataAreScaledForStarNetAndBack() throws {
        let exe = try FakeStarNet.make(in: dir)
        let plane = Synth.channel(width: 512, height: 512)
        plane.affine(scale: 65535, offset: 0)
        let r = try StarRemoval.run(planes: [plane], options: StarRemovalOptions(), tool: StarNetTool(executable: exe), workDirectory: dir)
        // What StarNet2 got was normalised to [0, 1].
        let sent = try FITSReader.read(url: dir.appendingPathComponent("input.fits")).planes[0]
        XCTAssertLessThanOrEqual(sent.minMax().max, 1)
        XCTAssertEqual(r.starless[0].minMax().max, plane.minMax().max, accuracy: 0.05)
    }

    func testAppStretchIsUndoneAfterwards() throws {
        let exe = try FakeStarNet.make(in: dir)
        let plane = Synth.channel(width: 512, height: 512)
        for method in [StretchMethod.autoSTF, .arcsinh, .ghs] {
            var opts = StarRemovalOptions(method: .appStretch)
            opts.stretch.method = method
            let r = try StarRemoval.run(planes: [plane], options: opts, tool: StarNetTool(executable: exe), workDirectory: dir)
            XCTAssertEqual(r.processing, .appStretch)
            // StarNet2 received stretched data…
            let sent = try FITSReader.read(url: dir.appendingPathComponent("input.fits")).planes[0]
            XCTAssertGreaterThan(Statistics.compute(image: sent).median, 0.05, "\(method)")
            // …and an unchanged (copied) result comes back as the original linear data, with nothing left as stars
            // except where the stretch clipped the highlights.
            let s = Statistics.compute(image: r.starless[0]), o = Statistics.compute(image: plane)
            XCTAssertEqual(s.median, o.median, accuracy: o.madn * 0.02, "\(method)")
            XCTAssertLessThan(Statistics.compute(image: r.stars[0]).percentile(0.99), o.madn * 0.05, "\(method)")
            let call = try XCTUnwrap(FakeStarNet.calls(in: dir).last)
            XCTAssertFalse(call.contains("--linear"), call)
        }
    }

    func testStretchedDataAreProcessedAsTheyAre() throws {
        let exe = try FakeStarNet.make(in: dir)
        let plane = Synth.channel(width: 512, height: 512, background: 0.2, nebula: 0.3)
        let r = try StarRemoval.run(planes: [plane, plane, plane], options: StarRemovalOptions(), tool: StarNetTool(executable: exe), workDirectory: dir)
        XCTAssertEqual(r.processing, .alreadyStretched)
        XCTAssertEqual(r.starless.count, 3)
        XCTAssertEqual(r.stars.count, 3)
        let call = try XCTUnwrap(FakeStarNet.calls(in: dir).last)
        XCTAssertTrue(call.contains("--unscreen"), call)
        XCTAssertFalse(call.contains("--linear"), call)
    }

    func testRejectsSmallImages() throws {
        let tool = StarNetTool(executable: try FakeStarNet.make(in: dir))
        XCTAssertThrowsError(try StarRemoval.run(planes: [Synth.channel(width: 400, height: 600)], options: StarRemovalOptions(), tool: tool, workDirectory: dir)) { e in
            XCTAssertEqual(e as? StarNetError, .imageTooSmall(400, 600))
        }
        XCTAssertTrue(FakeStarNet.calls(in: dir).isEmpty)
    }

    func testProcessFileWritesBackgroundAndStarsFiles() throws {
        let exe = try FakeStarNet.make(in: dir)
        let src = dir.appendingPathComponent("M1-Ha.fits")
        try FITSWriter.write(planes: [Synth.channel(width: 512, height: 512)], to: src, extraCards: [("FILTER", "Ha", nil), ("EXPTIME", "300", nil)])
        let before = try Data(contentsOf: src)
        let r = try StarRemoval.processFile(src, options: StarRemovalOptions(), tool: StarNetTool(executable: exe))
        XCTAssertEqual(r.starless.lastPathComponent, "M1-Ha_bg.fits")
        XCTAssertEqual(r.stars.lastPathComponent, "M1-Ha_stars.fits")
        let bg = try ImageLoader.load(url: r.starless)
        XCTAssertEqual(bg.filterName, "Ha", "acquisition keywords are carried over")
        XCTAssertEqual(bg.detectedKind, .ha)
        XCTAssertEqual(try FITSReader.read(url: r.stars).header.string("IMAGETYP"), "Stars")
        XCTAssertEqual(try Data(contentsOf: src), before, "the source is never modified")
        // A TIFF source gives float TIFF outputs for linear data.
        let tif = dir.appendingPathComponent("rgb.tif")
        let p = Synth.channel(width: 512, height: 512)
        try RasterWriter.write(planes: [p, p, p], to: tif, format: .tiff16)
        let out = dir.appendingPathComponent("out", isDirectory: true)
        try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        let t = try StarRemoval.processFile(tif, options: StarRemovalOptions(), tool: StarNetTool(executable: exe), folder: out)
        XCTAssertEqual(t.starless, out.appendingPathComponent("rgb_bg.tif"))
        let loaded = try ImageLoader.load(url: t.starless)
        XCTAssertEqual(loaded.planes.count, 3)
        XCTAssertTrue(loaded.formatDescription.contains("32-bit float"), loaded.formatDescription)
    }

    func testCancelStopsTheProgram() throws {
        let tool = StarNetTool(executable: try FakeStarNet.make(in: dir, behaviour: .slow))
        let cancel = StarNetCancellation()
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.5) { cancel.cancel() }
        let t0 = Date()
        XCTAssertThrowsError(try StarRemoval.run(planes: [Synth.channel(width: 512, height: 512)], options: StarRemovalOptions(), tool: tool, workDirectory: dir, cancellation: cancel)) { e in
            XCTAssertEqual(e as? StarNetError, .cancelled)
        }
        XCTAssertLessThan(Date().timeIntervalSince(t0), 10)
        // A cancelled token refuses to start another run.
        XCTAssertThrowsError(try tool.run(StarNetCommand(input: dir, output: dir.appendingPathComponent("x")), cancellation: cancel))
    }

    func testFindsTheExecutableInsideAnUnpackedArchive() throws {
        let pkg = dir.appendingPathComponent("starnet2_macos-arm64_cli/inner", isDirectory: true)
        try FileManager.default.createDirectory(at: pkg.appendingPathComponent("StarNet2_weights.mlpackage"), withIntermediateDirectories: true)
        let exe = try FakeStarNet.make(in: pkg)
        try "terms".write(to: pkg.appendingPathComponent("LICENSE.txt"), atomically: true, encoding: .utf8)
        XCTAssertEqual(StarNet.findExecutable(in: dir)?.standardizedFileURL, exe.standardizedFileURL)
        XCTAssertEqual(StarNet.licenseFile(for: exe)?.lastPathComponent, "LICENSE.txt")
        XCTAssertNil(StarNet.findExecutable(in: dir.appendingPathComponent("starnet2_macos-arm64_cli/inner/StarNet2_weights.mlpackage")))
    }
}
