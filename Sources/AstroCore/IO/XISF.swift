import Foundation
import Compression

public enum XISFError: Error, LocalizedError {
    case notXISF
    case truncated
    case unsupported(String)

    public var errorDescription: String? {
        switch self {
        case .notXISF: return "Not an XISF file."
        case .truncated: return "XISF file is truncated."
        case .unsupported(let s): return "Unsupported XISF: \(s)"
        }
    }
}

/// Result of loading the first image of a monolithic XISF file.
public struct XISFImage {
    /// One plane per channel (1 for grayscale, 3 for RGB), rows top-down, samples in [0,1].
    public let planes: [PlanarImage]
    /// The image's `FITSKeyword` elements as a FITS header (values as written, quotes included).
    public let header: FITSHeader
    /// XISF properties with a text or scalar value (`Instrument:Filter:Name`, …).
    public let properties: [String: String]
    /// `Float32`, `UInt16`, …
    public let sampleFormat: String
    /// `zlib`, `lz4`, `lz4hc`, `zstd` (with `+sh` when byte-shuffled) or nil.
    public let compression: String?
    public var width: Int { planes[0].width }
    public var height: Int { planes[0].height }
}

/// Reader for monolithic XISF 1.0 files (PixInsight's native format): the first `Image` element,
/// attached or inline data, planar or interleaved storage, UInt8/16/32/64 and Float32/64 samples,
/// uncompressed, zlib or LZ4 compression (optionally byte-shuffled). Zstandard is not supported.
public enum XISFReader {
    static let signature = Array("XISF0100".utf8)

    public static func read(url: URL) throws -> XISFImage {
        let data = try Data(contentsOf: url, options: [.alwaysMapped])
        return try read(data: data)
    }

    public static func read(data: Data) throws -> XISFImage {
        guard data.count >= 16, data.starts(with: signature) else { throw XISFError.notXISF }
        let headerLength = Int(data[8]) | Int(data[9]) << 8 | Int(data[10]) << 16 | Int(data[11]) << 24
        guard data.count >= 16 + headerLength else { throw XISFError.truncated }
        let xml = try XMLDocument(data: data.subdata(in: 16..<(16 + headerLength)), options: [])
        guard let root = xml.rootElement() else { throw XISFError.notXISF }
        guard let image = firstElement(named: "Image", in: root) else { throw XISFError.unsupported("no Image element") }
        func attr(_ name: String) -> String? { image.attribute(forName: name)?.stringValue }

        // Geometry "W:H:C" (XISF allows more dimensions; only 2-D images with channels are read).
        let geometry = (attr("geometry") ?? "").split(separator: ":").compactMap { Int($0) }
        guard geometry.count == 3, geometry[0] > 0, geometry[1] > 0, geometry[2] > 0 else { throw XISFError.unsupported("geometry \(attr("geometry") ?? "missing")") }
        let w = geometry[0], h = geometry[1], channels = geometry[2]
        guard channels <= 4 else { throw XISFError.unsupported("\(channels) channels") }
        let sampleFormat = attr("sampleFormat") ?? "Float32"
        let bytesPer: Int
        switch sampleFormat {
        case "UInt8": bytesPer = 1
        case "UInt16": bytesPer = 2
        case "UInt32", "Float32": bytesPer = 4
        case "UInt64", "Float64": bytesPer = 8
        default: throw XISFError.unsupported("sample format \(sampleFormat)")
        }
        let planar = (attr("pixelStorage") ?? "Planar") != "Normal"
        let bigEndian = (attr("byteOrder") ?? "little") == "big"
        let expected = w * h * channels * bytesPer

        // Data block: attachment or inline.
        let location = (attr("location") ?? "").split(separator: ":").map(String.init)
        var raw: Data
        switch location.first {
        case "attachment":
            guard location.count >= 3, let pos = Int(location[1]), let size = Int(location[2]) else { throw XISFError.unsupported("location \(attr("location") ?? "")") }
            guard pos >= 0, size >= 0, pos + size <= data.count else { throw XISFError.truncated }
            raw = data.subdata(in: pos..<(pos + size))
        case "inline":
            guard location.count >= 2, location[1] == "base64", let text = image.stringValue, let d = Data(base64Encoded: text.filter { !$0.isWhitespace }) else { throw XISFError.unsupported("inline encoding") }
            raw = d
        default: throw XISFError.unsupported("location \(attr("location") ?? "missing")")
        }

        // Compression "codec[+sh]:uncompressedSize[:itemSize]".
        let compression = attr("compression")
        if let c = compression {
            let parts = c.split(separator: ":").map(String.init)
            guard let size = parts.count > 1 ? Int(parts[1]) : nil else { throw XISFError.unsupported("compression \(c)") }
            let codec = parts[0].hasSuffix("+sh") ? String(parts[0].dropLast(3)) : parts[0]
            let shuffled = parts[0].hasSuffix("+sh")
            let itemSize = shuffled ? (parts.count > 2 ? Int(parts[2]) ?? bytesPer : bytesPer) : 1
            switch codec {
            case "zlib": raw = try inflate(raw, uncompressedSize: size)
            case "lz4", "lz4hc": raw = try LZ4.decodeBlock(raw, uncompressedSize: size)
            default: throw XISFError.unsupported("\(codec) compression (save the file uncompressed or with zlib / LZ4)")
            }
            if shuffled && itemSize > 1 { raw = unshuffle(raw, itemSize: itemSize) }
        }
        guard raw.count >= expected else { throw XISFError.truncated }

        // Samples → planes in [0,1].
        var planes: [PlanarImage] = []
        let count = w * h
        for c in 0..<channels {
            let plane = PlanarImage(width: w, height: h)
            raw.withUnsafeBytes { (buf: UnsafeRawBufferPointer) in
                let base = buf.baseAddress!
                let start = planar ? c * count * bytesPer : c * bytesPer
                let stride = planar ? 1 : channels
                decode(base: base.advanced(by: start), count: count, stride: stride, format: sampleFormat, bigEndian: bigEndian, into: plane.pointer)
            }
            planes.append(plane)
        }
        // Floating-point data outside [0,1] (bounds="lo:hi") is rescaled; integers are already normalised.
        if sampleFormat.hasPrefix("Float"), let b = attr("bounds") {
            let lohi = b.split(separator: ":").compactMap { Double($0) }
            if lohi.count == 2, lohi[1] > lohi[0], !(lohi[0] == 0 && lohi[1] == 1) {
                let a = Float(1 / (lohi[1] - lohi[0]))
                planes.forEach { $0.affine(scale: a, offset: -Float(lohi[0]) * a) }
            }
        }

        var header = FITSHeader()
        for k in image.elements(forName: "FITSKeyword") {
            guard let name = k.attribute(forName: "name")?.stringValue else { continue }
            header.append(FITSHeader.Card(key: name, value: k.attribute(forName: "value")?.stringValue, comment: k.attribute(forName: "comment")?.stringValue))
        }
        var properties: [String: String] = [:]
        for p in image.elements(forName: "Property") + root.elements(forName: "Property") {
            guard let id = p.attribute(forName: "id")?.stringValue, properties[id] == nil else { continue }
            if let v = p.attribute(forName: "value")?.stringValue { properties[id] = v }
            else if p.attribute(forName: "location") == nil, let t = p.stringValue, !t.isEmpty { properties[id] = t }
        }
        return XISFImage(planes: planes, header: header, properties: properties, sampleFormat: sampleFormat, compression: compression?.split(separator: ":").first.map(String.init))
    }

    /// Filter name: the FITS keyword, else the XISF filter property.
    public static func filterName(_ img: XISFImage) -> String? {
        img.header.string("FILTER") ?? img.header.string("FILT-1") ?? img.properties["Instrument:Filter:Name"]
    }

    // MARK: - Pieces

    static func firstElement(named name: String, in root: XMLElement) -> XMLElement? {
        if let e = root.elements(forName: name).first { return e }
        for child in root.children ?? [] {
            if let e = child as? XMLElement, let found = firstElement(named: name, in: e) { return found }
        }
        return nil
    }

    static func decode(base: UnsafeRawPointer, count: Int, stride: Int, format: String, bigEndian: Bool, into out: UnsafeMutablePointer<Float>) {
        switch format {
        case "UInt8":
            let p = base.assumingMemoryBound(to: UInt8.self)
            for i in 0..<count { out[i] = Float(p[i * stride]) / 255 }
        case "UInt16":
            let p = base.assumingMemoryBound(to: UInt16.self)
            for i in 0..<count { let v = p[i * stride]; out[i] = Float(bigEndian ? UInt16(bigEndian: v) : UInt16(littleEndian: v)) / 65535 }
        case "UInt32":
            let p = base.assumingMemoryBound(to: UInt32.self)
            for i in 0..<count { let v = p[i * stride]; out[i] = Float(Double(bigEndian ? UInt32(bigEndian: v) : UInt32(littleEndian: v)) / 4294967295.0) }
        case "UInt64":
            let p = base.assumingMemoryBound(to: UInt64.self)
            for i in 0..<count { let v = p[i * stride]; out[i] = Float(Double(bigEndian ? UInt64(bigEndian: v) : UInt64(littleEndian: v)) / 18446744073709551615.0) }
        case "Float32":
            let p = base.assumingMemoryBound(to: UInt32.self)
            for i in 0..<count { let v = p[i * stride]; out[i] = Float(bitPattern: bigEndian ? UInt32(bigEndian: v) : UInt32(littleEndian: v)) }
        case "Float64":
            let p = base.assumingMemoryBound(to: UInt64.self)
            for i in 0..<count { let v = p[i * stride]; out[i] = Float(Double(bitPattern: bigEndian ? UInt64(bigEndian: v) : UInt64(littleEndian: v))) }
        default: break
        }
    }

    /// zlib stream (RFC 1950: 2-byte header, deflate data, Adler-32) → bytes.
    static func inflate(_ src: Data, uncompressedSize: Int) throws -> Data {
        guard src.count > 2 else { throw XISFError.truncated }
        var out = Data(count: uncompressedSize)
        let n = out.withUnsafeMutableBytes { (dst: UnsafeMutableRawBufferPointer) -> Int in
            src.withUnsafeBytes { (s: UnsafeRawBufferPointer) -> Int in
                compression_decode_buffer(dst.baseAddress!.assumingMemoryBound(to: UInt8.self), uncompressedSize,
                                          s.baseAddress!.advanced(by: 2).assumingMemoryBound(to: UInt8.self), src.count - 2, nil, COMPRESSION_ZLIB)
            }
        }
        guard n == uncompressedSize else { throw XISFError.unsupported("zlib stream does not inflate to \(uncompressedSize) bytes") }
        return out
    }

    /// Undoes XISF byte shuffling: byte j of every item was stored contiguously.
    static func unshuffle(_ src: Data, itemSize: Int) -> Data {
        let n = src.count / itemSize
        var out = Data(count: src.count)
        out.withUnsafeMutableBytes { (d: UnsafeMutableRawBufferPointer) in
            src.withUnsafeBytes { (s: UnsafeRawBufferPointer) in
                for j in 0..<itemSize {
                    let col = j * n
                    for i in 0..<n { d[i * itemSize + j] = s[col + i] }
                }
                for k in (n * itemSize)..<src.count { d[k] = s[k] }
            }
        }
        return out
    }
}

/// LZ4 block format (no frame), as used by XISF.
public enum LZ4 {
    public static func decodeBlock(_ src: Data, uncompressedSize: Int) throws -> Data {
        var out = Data(count: uncompressedSize)
        try out.withUnsafeMutableBytes { (d: UnsafeMutableRawBufferPointer) in
            try src.withUnsafeBytes { (s: UnsafeRawBufferPointer) in
                let n = s.count
                var i = 0, o = 0
                while i < n {
                    let token = Int(s[i]); i += 1
                    var literals = token >> 4
                    if literals == 15 {
                        while true { guard i < n else { throw XISFError.truncated }; let b = Int(s[i]); i += 1; literals += b; if b != 255 { break } }
                    }
                    guard i + literals <= n, o + literals <= uncompressedSize else { throw XISFError.truncated }
                    if literals > 0 { d.baseAddress!.advanced(by: o).copyMemory(from: s.baseAddress!.advanced(by: i), byteCount: literals) }
                    i += literals; o += literals
                    if i >= n { break }   // the last sequence carries literals only
                    guard i + 2 <= n else { throw XISFError.truncated }
                    let offset = Int(s[i]) | Int(s[i + 1]) << 8; i += 2
                    guard offset > 0, offset <= o else { throw XISFError.unsupported("corrupt LZ4 block") }
                    var length = (token & 15) + 4
                    if token & 15 == 15 {
                        while true { guard i < n else { throw XISFError.truncated }; let b = Int(s[i]); i += 1; length += b; if b != 255 { break } }
                    }
                    guard o + length <= uncompressedSize else { throw XISFError.truncated }
                    var from = o - offset
                    for _ in 0..<length { d[o] = d[from]; o += 1; from += 1 }   // byte-wise: matches may overlap
                }
                guard o == uncompressedSize else { throw XISFError.truncated }
            }
        }
        return out
    }
}
