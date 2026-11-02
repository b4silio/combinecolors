import XCTest
import Compression
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

final class XISFTests: XCTestCase {
    /// Builds a monolithic XISF file around one Image element.
    static func xisf(imageAttributes: String, children: String = "", payload: Data) -> Data {
        let dataStart = 16 + 4096   // header block padded to 4 KiB, as PixInsight does
        let xml = """
        <?xml version="1.0" encoding="UTF-8"?><xisf version="1.0" xmlns="http://www.pixinsight.com/xisf"><Image \(imageAttributes) location="attachment:\(dataStart):\(payload.count)">\(children)</Image></xisf>
        """
        var header = Data(xml.utf8)
        header.append(Data(repeating: 0x20, count: 4096 - header.count))
        var d = Data("XISF0100".utf8)
        let len = UInt32(4096)
        d.append(contentsOf: withUnsafeBytes(of: len.littleEndian) { Array($0) })
        d.append(Data(count: 4))
        d.append(header)
        d.append(payload)
        return d
    }

    static func floatBytes(_ p: PlanarImage) -> Data {
        var d = Data(count: p.count * 4)
        d.withUnsafeMutableBytes { $0.baseAddress!.copyMemory(from: p.pointer, byteCount: p.count * 4) }
        return d
    }

    /// zlib stream: header, raw deflate (Compression framework), Adler-32.
    static func zlib(_ src: Data) -> Data {
        var dst = Data(count: src.count + 1024)
        let n = dst.withUnsafeMutableBytes { (d: UnsafeMutableRawBufferPointer) in
            src.withUnsafeBytes { (s: UnsafeRawBufferPointer) in
                compression_encode_buffer(d.baseAddress!.assumingMemoryBound(to: UInt8.self), d.count, s.baseAddress!.assumingMemoryBound(to: UInt8.self), src.count, nil, COMPRESSION_ZLIB)
            }
        }
        var a: UInt32 = 1, b: UInt32 = 0
        for byte in src { a = (a + UInt32(byte)) % 65521; b = (b + a) % 65521 }
        var out = Data([0x78, 0x9C])
        out.append(dst.prefix(n))
        let adler = (b << 16) | a
        out.append(contentsOf: withUnsafeBytes(of: adler.bigEndian) { Array($0) })
        return out
    }

    static func shuffle(_ src: Data, itemSize: Int) -> Data {
        let n = src.count / itemSize
        var out = Data(count: src.count)
        for i in 0..<n { for j in 0..<itemSize { out[j * n + i] = src[i * itemSize + j] } }
        for k in (n * itemSize)..<src.count { out[k] = src[k] }
        return out
    }

    /// LZ4 block with literals only (valid, just not compressed) plus one real match at the end.
    static func lz4(_ src: Data) -> Data {
        var out = Data()
        func lengthBytes(_ n: Int, into d: inout Data) { var r = n - 15; while r >= 255 { d.append(255); r -= 255 }; d.append(UInt8(r)) }
        // Sequence 1: all bytes but the last 8 as literals, then a match of 8 bytes copying the previous 8.
        let lit = src.count - 8
        let token = UInt8(min(lit, 15) << 4) | UInt8(8 - 4)
        out.append(token)
        if lit >= 15 { lengthBytes(lit, into: &out) }
        out.append(src.prefix(lit))
        out.append(contentsOf: [8, 0])   // offset 8 → repeats the 8 bytes before
        // Sequence 2 (last): no literals.
        out.append(0)
        return out
    }

    func testFloat32GrayWithFITSKeywordsLoadsAndDetectsTheFilter() throws {
        let img = Synth.channel(width: 41, height: 13)
        let file = XISFTests.xisf(imageAttributes: #"geometry="41:13:1" sampleFormat="Float32" bounds="0:1" colorSpace="Gray""#,
                                  children: #"<FITSKeyword name="FILTER" value="'Ha'" comment="Name of filter"/><Property id="Instrument:Filter:Name" type="String">Ha</Property>"#,
                                  payload: XISFTests.floatBytes(img))
        let x = try XISFReader.read(data: file)
        XCTAssertEqual(x.width, 41); XCTAssertEqual(x.height, 13); XCTAssertEqual(x.planes.count, 1)
        XCTAssertEqual(x.planes[0].maxAbsDifference(img), 0)
        XCTAssertEqual(x.header.string("FILTER"), "Ha")
        XCTAssertEqual(x.properties["Instrument:Filter:Name"], "Ha")
        XCTAssertEqual(XISFReader.filterName(x), "Ha")
        XCTAssertNil(x.compression)
        let url = Synth.tempURL("gray.xisf")
        try file.write(to: url)
        let loaded = try ImageLoader.load(url: url)
        XCTAssertEqual(loaded.detectedKind, .ha)
        XCTAssertEqual(loaded.formatDescription, "XISF 32-bit float")
        XCTAssertTrue(ImageLoader.isSupported(url))
    }

    func testUInt16RGBPlanarZlibShuffledAndBigEndian() throws {
        let w = 7, h = 5
        var samples = Data()
        for c in 0..<3 { for _ in 0..<(w * h) { let v = UInt16(c == 0 ? 16384 : c == 1 ? 32768 : 65535); samples.append(contentsOf: withUnsafeBytes(of: v.bigEndian) { Array($0) }) } }
        let payload = XISFTests.zlib(XISFTests.shuffle(samples, itemSize: 2))
        let file = XISFTests.xisf(imageAttributes: #"geometry="7:5:3" sampleFormat="UInt16" colorSpace="RGB" byteOrder="big" compression="zlib+sh:\#(samples.count):2""#, payload: payload)
        let x = try XISFReader.read(data: file)
        XCTAssertEqual(x.planes.count, 3)
        XCTAssertEqual(x.planes[0][3, 2], 16384 / 65535, accuracy: 1e-6)
        XCTAssertEqual(x.planes[1][0, 0], 32768 / 65535, accuracy: 1e-6)
        XCTAssertEqual(x.planes[2][6, 4], 1, accuracy: 1e-6)
        XCTAssertEqual(x.compression, "zlib+sh")
    }

    func testFloat32InterleavedLZ4AndBoundsRescale() throws {
        let w = 6, h = 4
        var samples = Data()
        for i in 0..<(w * h) { for c in 0..<3 { let v = Float(100 * c + i); samples.append(contentsOf: withUnsafeBytes(of: v.bitPattern.littleEndian) { Array($0) }) } }
        // The last 8 bytes must repeat the 8 before them for the synthetic LZ4 match: patch them.
        samples.replaceSubrange((samples.count - 8)..., with: samples[(samples.count - 16)..<(samples.count - 8)])
        let file = XISFTests.xisf(imageAttributes: #"geometry="6:4:3" sampleFormat="Float32" pixelStorage="Normal" bounds="0:400" compression="lz4:\#(samples.count)""#, payload: XISFTests.lz4(samples))
        let x = try XISFReader.read(data: file)
        XCTAssertEqual(x.planes.count, 3)
        XCTAssertEqual(x.planes[0][1, 0], 1 / 400, accuracy: 1e-6)
        XCTAssertEqual(x.planes[1][1, 0], 101 / 400, accuracy: 1e-6)
        XCTAssertEqual(x.planes[2][0, 0], 200 / 400, accuracy: 1e-6)
    }

    func testRejectsNonXISFAndUnsupportedCompression() {
        XCTAssertThrowsError(try XISFReader.read(data: Data("SIMPLE  = T".utf8)))
        let file = XISFTests.xisf(imageAttributes: #"geometry="2:2:1" sampleFormat="Float32" compression="zstd:16""#, payload: Data(count: 16))
        XCTAssertThrowsError(try XISFReader.read(data: file)) { e in XCTAssertTrue("\(e.localizedDescription)".contains("zstd")) }
    }

    func testRealPixInsightFiles() throws {
        let dir = "/Users/basilio/Pictures/Astrophotography/_Processing/Tino/"
        try XCTSkipUnless(FileManager.default.fileExists(atPath: dir + "Stack_Ha.xisf"), "sample files not present")
        for (name, kind, filter) in [("Stack_Ha.xisf", ChannelKind.ha, "Ha"), ("Stack_Sii.xisf", .sii, "SII")] {
            let loaded = try ImageLoader.load(url: URL(fileURLWithPath: dir + name))
            XCTAssertEqual(loaded.width, 6224); XCTAssertEqual(loaded.height, 4168); XCTAssertEqual(loaded.planes.count, 1)
            XCTAssertEqual(loaded.detectedKind, kind)
            XCTAssertEqual(loaded.filterName, filter)
            XCTAssertEqual(loaded.formatDescription, "XISF 32-bit float")
            let (lo, hi) = loaded.planes[0].minMax()
            XCTAssertGreaterThanOrEqual(lo, 0); XCTAssertLessThanOrEqual(hi, 1); XCTAssertGreaterThan(hi, lo)
            XCTAssertEqual(loaded.header?.string("OBJECT"), "M 43")
        }
    }
}
