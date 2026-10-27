import XCTest
@testable import AstroCore

final class FITSTests: XCTestCase {
    func testHeaderCardParsing() {
        let c = FITSHeader.parseCard("FILTER  = 'Hydrogen/alpha'     / filter used                                   ")!
        XCTAssertEqual(c.key, "FILTER")
        var h = FITSHeader(); h.append(c)
        XCTAssertEqual(h.string("FILTER"), "Hydrogen/alpha")
        XCTAssertEqual(c.comment, "filter used")
        let n = FITSHeader.parseCard("NAXIS1  =                 7272 / n'th data dimension                            ")!
        h.append(n)
        XCTAssertEqual(h.int("NAXIS1"), 7272)
        let d = FITSHeader.parseCard("BZERO   =                  0.E0 / no offset                                     ")!
        h.append(d)
        XCTAssertEqual(h.double("BZERO"), 0)
        let q = FITSHeader.parseCard("NAME    = 'it''s'              / quoted                                        ")!
        h.append(q)
        XCTAssertEqual(h.string("NAME"), "it's")
    }

    func testFloat32RoundTrip() throws {
        let img = Synth.channel(width: 37, height: 11)
        let url = Synth.tempURL("rt32.fits")
        try FITSWriter.write(planes: [img], to: url, depth: .float32, extraCards: [("FILTER", "Ha", "filter")])
        let back = try FITSReader.read(url: url)
        XCTAssertEqual(back.width, 37); XCTAssertEqual(back.height, 11); XCTAssertEqual(back.bitpix, -32)
        XCTAssertEqual(back.planes[0].maxAbsDifference(img), 0)
        XCTAssertEqual(back.header.string("FILTER"), "Ha")
        XCTAssertEqual(back.header.string("ROWORDER"), "TOP-DOWN")
        XCTAssertEqual(try FITSReader.readHeader(url: url).int("NAXIS1"), 37)
    }

    func testUInt16RoundTripAndRGB() throws {
        let r = PlanarImage(width: 5, height: 3, fill: 0.25), g = PlanarImage(width: 5, height: 3, fill: 0.5), b = PlanarImage(width: 5, height: 3, fill: 1.0)
        let url = Synth.tempURL("rt16.fits")
        try FITSWriter.write(planes: [r, g, b], to: url, depth: .uint16)
        let back = try FITSReader.read(url: url)
        XCTAssertEqual(back.planes.count, 3)
        XCTAssertEqual(back.header.int("NAXIS3"), 3)
        XCTAssertEqual(back.planes[0][0, 0], 0.25, accuracy: 1 / 65535)
        XCTAssertEqual(back.planes[1][2, 2], 0.5, accuracy: 1 / 65535)
        XCTAssertEqual(back.planes[2][4, 2], 1.0, accuracy: 1 / 65535)
    }

    func testSyntheticInt16WithoutBZEROAndBottomUp() throws {
        // Build a minimal signed 16-bit FITS by hand with ROWORDER = BOTTOM-UP.
        var header = ""
        func card(_ s: String) { header += s.padding(toLength: 80, withPad: " ", startingAt: 0) }
        card("SIMPLE  =                    T")
        card("BITPIX  =                   16")
        card("NAXIS   =                    2")
        card("NAXIS1  =                    2")
        card("NAXIS2  =                    2")
        card("ROWORDER= 'BOTTOM-UP'")
        card("END")
        while header.count % 2880 != 0 { header += " " }
        var data = Data(header.utf8)
        // rows stored bottom-up: first stored row is the bottom of the image.
        let values: [Int16] = [-32768, 0, 32767, -16384]
        for v in values { var be = UInt16(bitPattern: v).bigEndian; data.append(Data(bytes: &be, count: 2)) }
        data.append(Data(count: 2880 - 8))
        let img = try FITSReader.read(data: data)
        // After flip the top row is the second stored row.
        XCTAssertEqual(img.planes[0][0, 0], 1.0, accuracy: 1e-4)        // 32767 + 32768 → 65535
        XCTAssertEqual(img.planes[0][1, 0], 0.25, accuracy: 1e-4)       // -16384 + 32768 → 16384
        XCTAssertEqual(img.planes[0][0, 1], 0.0, accuracy: 1e-4)
        XCTAssertEqual(img.planes[0][1, 1], 0.5, accuracy: 1e-4)
    }

    func testRejectsNonFITS() {
        XCTAssertThrowsError(try FITSReader.read(data: Data(repeating: 0, count: 5000)))
    }
}

final class RasterTests: XCTestCase {
    func testTIFF16GrayRoundTrip() throws {
        let img = PlanarImage(width: 16, height: 8)
        for i in 0..<img.count { img.pointer[i] = Float(i) / Float(img.count - 1) }
        let url = Synth.tempURL("gray16.tif")
        try RasterWriter.write(planes: [img], to: url, format: .tiff16)
        let back = try RasterReader.read(url: url)
        XCTAssertEqual(back.planes.count, 1)
        XCTAssertEqual(back.bitsPerComponent, 16)
        XCTAssertLessThan(back.planes[0].maxAbsDifference(img), 1.0 / 65535)
        let loaded = try ImageLoader.load(url: url)
        XCTAssertEqual(loaded.formatDescription, "TIF 16-bit")
    }

    func testTIFF16RGBRoundTrip() throws {
        let r = PlanarImage(width: 9, height: 7, fill: 0.1), g = PlanarImage(width: 9, height: 7, fill: 0.6), b = PlanarImage(width: 9, height: 7, fill: 0.9)
        let url = Synth.tempURL("rgb16.tif")
        try RasterWriter.write(planes: [r, g, b], to: url, format: .tiff16)
        let back = try RasterReader.read(url: url)
        XCTAssertEqual(back.planes.count, 3)
        XCTAssertEqual(back.planes[0][4, 3], 0.1, accuracy: 1e-4)
        XCTAssertEqual(back.planes[1][4, 3], 0.6, accuracy: 1e-4)
        XCTAssertEqual(back.planes[2][4, 3], 0.9, accuracy: 1e-4)
    }

    func testTIFF32FloatRoundTrip() throws {
        let img = Synth.channel(width: 20, height: 10)
        let url = Synth.tempURL("f32.tif")
        try RasterWriter.write(planes: [img], to: url, format: .tiff32)
        let back = try RasterReader.read(url: url)
        XCTAssertTrue(back.isFloat)
        XCTAssertLessThan(back.planes[0].maxAbsDifference(img), 1e-6)
    }

    func testPNG16AndJPEG() throws {
        let img = PlanarImage(width: 8, height: 8, fill: 0.5)
        let url = Synth.tempURL("x.png")
        try RasterWriter.write(planes: [img, img, img], to: url, format: .png16)
        let back = try RasterReader.read(url: url)
        XCTAssertEqual(back.planes[0][1, 1], 0.5, accuracy: 1e-3)
        let jurl = Synth.tempURL("x.jpg")
        try RasterWriter.write(planes: [img, img, img], to: jurl, format: .jpeg)
        XCTAssertTrue(FileManager.default.fileExists(atPath: jurl.path))
    }

    func testDisplayImage() {
        let rgb = RGBImage(width: 4, height: 4)
        rgb.r.pointer[0] = 1; rgb.g.pointer[0] = 0.5; rgb.b.pointer[0] = 2  // clamped
        let cg = RasterWriter.makeDisplayImage(rgb: rgb)
        XCTAssertNotNil(cg)
        XCTAssertEqual(cg?.width, 4)
    }
}

final class ChannelDetectionTests: XCTestCase {
    func testFilterKeyword() {
        XCTAssertEqual(ChannelDetection.detect(filterName: "Hydrogen-alpha"), .ha)
        XCTAssertEqual(ChannelDetection.detect(filterName: "Oxygen III"), .oiii)
        XCTAssertEqual(ChannelDetection.detect(filterName: "Sulphur II"), .sii)
        XCTAssertEqual(ChannelDetection.detect(filterName: "Red"), .red)
        XCTAssertEqual(ChannelDetection.detect(filterName: "L"), .luminance)
        XCTAssertEqual(ChannelDetection.detect(filterName: "Hb"), .hb)
        XCTAssertNil(ChannelDetection.detect(filterName: "Unknown-xyz"))
    }

    func testFileNames() {
        XCTAssertEqual(ChannelDetection.detect(fileName: "Soul_Core-Oxygen_III-session_1-lpc.fits"), .oiii)
        XCTAssertEqual(ChannelDetection.detect(fileName: "Soul_Core-Hydrogen-alpha-session_1-lpc.fits"), .ha)
        XCTAssertEqual(ChannelDetection.detect(fileName: "Soul_Core-Sulphur_II-session_1-lpc.fits"), .sii)
        XCTAssertEqual(ChannelDetection.detect(fileName: "LowersNebula-36h-Ha.tif"), .ha)
        XCTAssertEqual(ChannelDetection.detect(fileName: "LowersNebula-36h-O3.tif"), .oiii)
        XCTAssertEqual(ChannelDetection.detect(fileName: "LowersNebula-36h-S2.tif"), .sii)
        XCTAssertEqual(ChannelDetection.detect(fileName: "M31_R.fit"), .red)
        XCTAssertEqual(ChannelDetection.detect(fileName: "master_L.fits"), .luminance)
        XCTAssertEqual(ChannelDetection.detect(fileName: "ngc7000_SII_300s.fits"), .sii)
        XCTAssertEqual(ChannelDetection.detect(fileName: "Blue_stack.tif"), .blue)
        XCTAssertNil(ChannelDetection.detect(fileName: "stars.tif"))
        XCTAssertEqual(ChannelDetection.detect(fileName: "stars.tif", filterKeyword: "Green"), .green)
    }
}
