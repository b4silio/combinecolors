import XCTest
import AstroCore
@testable import CombineColors

/// Star removal through the app model, with a stand-in for StarNet2 (a shell script that copies
/// its input to every output: the real program is never run by the tests).
extension ProjectModelTests {
    func makeFakeStarNet(in folder: URL? = nil, slow: Bool = false, license: String? = "terms v1") throws -> URL {
        let d = folder ?? dir.appendingPathComponent("starnet", isDirectory: true)
        try FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        let exe = d.appendingPathComponent("starnet2")
        let script = #"""
        #!/bin/bash
        if [ "$1" = "--machine-info" ]; then
          echo '{"product":"starnet2","version":"2.6.2","build":"0242","backend":{"display_name":"CoreML"}}'
          exit 0
        fi
        in=""; out=""; mask=""
        while [ $# -gt 0 ]; do
          case "$1" in -i) in="$2"; shift;; -o) out="$2"; shift;; --mask) mask="$2"; shift;; esac
          shift
        done
        """# + (slow ? "\nsleep 20\n" : "\n") + #"""
        echo '{"schema":"starnetastro.cli.progress.v1","event":"progress","percent":50.0}' >&2
        cp "$in" "$out"; [ -n "$mask" ] && cp "$in" "$mask"
        exit 0
        """#
        try script.write(to: exe, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: exe.path)
        if let license { try license.write(to: d.appendingPathComponent("LICENSE.txt"), atomically: true, encoding: .utf8) }
        return exe
    }

    func starNetReadyModel(slow: Bool = false) async throws -> ProjectModel {
        let m = ProjectModel(defaults: defaults)
        m.loadFiles([try makeChannel("x-Ha.fits", kind: .ha, scale: 1, seed: 1, width: 512, height: 512),
                     try makeChannel("x-OIII.fits", kind: .oiii, scale: 0.4, seed: 2, width: 512, height: 512)])
        await wait { m.sources.count == 2 && !m.isLoading && m.context != nil }
        m.starNet.choose(try makeFakeStarNet(slow: slow))
        await wait { m.starNet.phase == .needsLicense }
        m.starNet.acceptLicense()
        return m
    }

    func testStarNetSetupAsksForTheLicenseAgainWhenItChanges() async throws {
        let setup = StarNetSetup(defaults: defaults, installRoot: dir.appendingPathComponent("install"))
        let exe = try makeFakeStarNet()
        setup.choose(exe)
        await wait { setup.phase == .needsLicense }
        XCTAssertEqual(setup.phase, .needsLicense)
        XCTAssertEqual(setup.licenseText, "terms v1")
        XCTAssertEqual(setup.info?.version, "2.6.2")
        setup.acceptLicense()
        XCTAssertEqual(setup.phase, .ready)
        // Remembered: a new session finds it ready.
        let again = StarNetSetup(defaults: defaults, installRoot: dir.appendingPathComponent("install"))
        again.refresh()
        await wait { again.phase == .ready }
        XCTAssertEqual(again.tool?.executable.path, exe.path)
        // Revised terms need a new acceptance.
        try "terms v2".write(to: exe.deletingLastPathComponent().appendingPathComponent("LICENSE.txt"), atomically: true, encoding: .utf8)
        again.refresh()
        await wait { again.phase == .needsLicense }
        XCTAssertEqual(again.phase, .needsLicense)
        // Something that is not StarNet2.
        let other = dir.appendingPathComponent("notes.txt")
        try "hello".write(to: other, atomically: true, encoding: .utf8)
        again.choose(other)
        await wait { if case .failed = again.phase { return true }; return false }
        guard case .failed = again.phase else { return XCTFail("\(again.phase)") }
    }

    func testStarNetInstallsFromADownloadedArchive() async throws {
        let pkg = dir.appendingPathComponent("pkg/starnet2_macos-arm64_cli", isDirectory: true)
        _ = try makeFakeStarNet(in: pkg)
        let zip = dir.appendingPathComponent("starnet2.zip")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        p.arguments = ["-c", "-k", "--keepParent", pkg.path, zip.path]
        try p.run(); p.waitUntilExit()
        let root = dir.appendingPathComponent("install", isDirectory: true)
        let setup = StarNetSetup(defaults: defaults, installRoot: root)

        setup.install(archive: zip, expectedSHA256: String(repeating: "0", count: 64), version: "2.6.2")
        await wait { if case .failed = setup.phase { return true }; return false }
        guard case .failed(let message) = setup.phase else { return XCTFail("\(setup.phase)") }
        XCTAssertTrue(message.contains("damaged"), message)

        setup.install(archive: zip, expectedSHA256: try StarNetSetup.sha256(of: zip), version: "2.6.2")
        await wait { setup.phase == .needsLicense }
        XCTAssertEqual(setup.tool?.executable.standardizedFileURL, root.appendingPathComponent("2.6.2/starnet2").standardizedFileURL)
        XCTAssertEqual(setup.licenseText, "terms v1")
        XCTAssertTrue(FileManager.default.fileExists(atPath: zip.path), "a file the user picked is not deleted")
    }

    func testStarRemovalSwitchesChannelsToTheBackgroundFiles() async throws {
        let m = try await starNetReadyModel()
        XCTAssertEqual(m.starNet.phase, .ready)
        // A painted correction on Ha must follow the channel to its new file.
        m.beginCorrectionEditing(.ha)
        m.correctionBrushRadius = 40
        m.correctionBrushFlow = 1
        m.beginCorrectionStroke()
        m.paintCorrection(from: CGPoint(x: 200, y: 200), to: CGPoint(x: 260, y: 200), invert: false)
        m.commitCorrectionEditing()
        await wait { m.hasCorrection(.ha) && !m.isLoading }
        XCTAssertTrue(m.hasCorrection(.ha))

        let items = m.starRemovalItems()
        XCTAssertEqual(items.map(\.kinds), [[.ha], [.oiii]])
        XCTAssertEqual(items.map { $0.starless.lastPathComponent }, ["x-Ha_bg.fits", "x-OIII_bg.fits"])
        XCTAssertFalse(items.contains { $0.alreadyStarless || $0.outputsExist })

        m.removeStars(items, options: StarRemovalOptions())
        XCTAssertTrue(m.isRemovingStars)
        await wait(timeout: 60) { !m.isRemovingStars && !m.isLoading && m.sources[.oiii]?.url?.lastPathComponent == "x-OIII_bg.fits" && m.statusText.contains("stars removed") }
        XCTAssertNil(m.errorMessage)
        XCTAssertEqual(m.sources[.ha]?.url?.lastPathComponent, "x-Ha_bg.fits")
        XCTAssertEqual(m.sources[.oiii]?.url?.lastPathComponent, "x-OIII_bg.fits")
        for name in ["x-Ha_stars.fits", "x-OIII_stars.fits", "x-Ha.fits", "x-OIII.fits"] {
            XCTAssertTrue(FileManager.default.fileExists(atPath: dir.appendingPathComponent(name).path), name)
        }
        XCTAssertTrue(m.hasCorrection(.ha), "the painted correction is copied to the _bg file")
        XCTAssertTrue(FileManager.default.fileExists(atPath: CorrectionMap.sidecarURL(for: dir.appendingPathComponent("x-Ha_bg.fits"), kind: .ha).path))
        XCTAssertEqual(Set(m.recentSets.first?.entries.map { ($0.path as NSString).lastPathComponent } ?? []), ["x-Ha_bg.fits", "x-OIII_bg.fits"])
        XCTAssertTrue(m.starRemovalItems().allSatisfy(\.alreadyStarless))
        XCTAssertTrue(m.statusText.contains("stars removed"), m.statusText)
        // The stars fill the Stars tab (not shown) and the combination is on.
        await wait { m.layerSources(.stars).count == 2 && m.starsLayer != nil && !m.starsCompositeBusy }
        XCTAssertEqual(m.activeLayer, .background)
        XCTAssertEqual(Set(m.layerSources(.stars).values.compactMap { $0.url?.lastPathComponent }), ["x-Ha_stars.fits", "x-OIII_stars.fits"])
        XCTAssertTrue(m.settings.stars.enabled)
        XCTAssertFalse(m.settings.stars.useMask)
        XCTAssertNotNil(m.starsLayer)
        XCTAssertEqual(m.projectDocument().starsLayer?.channels.count, 2)
    }

    func testStoppingStarRemovalLeavesTheChannelsAlone() async throws {
        let m = try await starNetReadyModel(slow: true)
        let before = m.sources[.ha]?.url
        m.removeStars(m.starRemovalItems(), options: StarRemovalOptions())
        await wait(timeout: 5) { (m.starRemovalProgress?.label.contains("x-Ha.fits") ?? false) }
        try await Task.sleep(nanoseconds: 300_000_000)
        m.stopStarRemoval()
        await wait(timeout: 10) { !m.isRemovingStars }
        XCTAssertFalse(m.isRemovingStars)
        XCTAssertEqual(m.sources[.ha]?.url, before)
        XCTAssertTrue(m.statusText.contains("stopped"), m.statusText)
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.appendingPathComponent("x-OIII_bg.fits").path), "the second file was never started")
    }
}
