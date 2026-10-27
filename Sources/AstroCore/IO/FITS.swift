import Foundation
import Accelerate

public enum FITSError: Error, LocalizedError {
    case notFITS
    case truncated
    case unsupported(String)
    case writeFailed(String)

    public var errorDescription: String? {
        switch self {
        case .notFITS: return "The file is not a FITS file."
        case .truncated: return "The FITS file is truncated."
        case .unsupported(let s): return "Unsupported FITS file: \(s)"
        case .writeFailed(let s): return "Could not write FITS file: \(s)"
        }
    }
}

/// A parsed FITS header: ordered cards plus keyword lookup.
public struct FITSHeader {
    public struct Card { public let key: String; public let value: String?; public let comment: String? }
    public private(set) var cards: [Card] = []
    private var index: [String: Int] = [:]

    public init() {}

    public mutating func append(_ c: Card) {
        if index[c.key] == nil { index[c.key] = cards.count }
        cards.append(c)
    }

    public subscript(key: String) -> String? {
        guard let i = index[key.uppercased()] else { return nil }
        return cards[i].value
    }

    public func string(_ key: String) -> String? {
        guard let v = self[key] else { return nil }
        var s = v.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("'") {
            s.removeFirst()
            if let q = s.lastIndex(of: "'") { s = String(s[..<q]) }
            s = s.replacingOccurrences(of: "''", with: "'")
        }
        return s.trimmingCharacters(in: .whitespaces)
    }

    public func int(_ key: String) -> Int? {
        guard let v = self[key] else { return nil }
        return Int(v.trimmingCharacters(in: .whitespaces))
    }

    public func double(_ key: String) -> Double? {
        guard let v = self[key] else { return nil }
        return Double(v.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "D", with: "E"))
    }

    public func bool(_ key: String) -> Bool? {
        guard let v = self[key]?.trimmingCharacters(in: .whitespaces) else { return nil }
        return v == "T" ? true : (v == "F" ? false : nil)
    }

    /// Parse one 80-character card.
    static func parseCard(_ s: String) -> Card? {
        let key = String(s.prefix(8)).trimmingCharacters(in: .whitespaces)
        if key.isEmpty { return nil }
        if key == "COMMENT" || key == "HISTORY" || key == "END" {
            return Card(key: key, value: nil, comment: String(s.dropFirst(8)).trimmingCharacters(in: .whitespaces))
        }
        guard s.count >= 10, s.dropFirst(8).hasPrefix("= ") || s.dropFirst(8).hasPrefix("=") else {
            return Card(key: key, value: nil, comment: String(s.dropFirst(8)).trimmingCharacters(in: .whitespaces))
        }
        let rest = String(s.dropFirst(9))
        // String values may contain '/', so find the closing quote first.
        var value = rest, comment: String? = nil
        let trimmed = rest.trimmingCharacters(in: .whitespaces)
        if trimmed.hasPrefix("'") {
            var i = trimmed.index(after: trimmed.startIndex)
            var closed = false
            while i < trimmed.endIndex {
                if trimmed[i] == "'" {
                    let next = trimmed.index(after: i)
                    if next < trimmed.endIndex && trimmed[next] == "'" { i = trimmed.index(after: next); continue }
                    closed = true
                    value = String(trimmed[...i])
                    let after = String(trimmed[trimmed.index(after: i)...]).trimmingCharacters(in: .whitespaces)
                    if after.hasPrefix("/") { comment = String(after.dropFirst()).trimmingCharacters(in: .whitespaces) }
                    break
                }
                i = trimmed.index(after: i)
            }
            if !closed { value = trimmed }
        } else if let slash = trimmed.firstIndex(of: "/") {
            value = String(trimmed[..<slash]).trimmingCharacters(in: .whitespaces)
            comment = String(trimmed[trimmed.index(after: slash)...]).trimmingCharacters(in: .whitespaces)
        } else {
            value = trimmed
        }
        return Card(key: key, value: value, comment: comment)
    }
}

/// Result of loading a FITS primary HDU.
public struct FITSImage {
    public let header: FITSHeader
    /// One plane per NAXIS3 slice (1 for mono, 3 for RGB).
    public let planes: [PlanarImage]
    public let bitpix: Int
    public var width: Int { planes[0].width }
    public var height: Int { planes[0].height }
}

public enum FITSReader {
    public static let blockSize = 2880

    public static func read(url: URL) throws -> FITSImage {
        let data = try Data(contentsOf: url, options: [.alwaysMapped])
        return try read(data: data)
    }

    public static func readHeader(url: URL) throws -> FITSHeader {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var header = FITSHeader()
        var done = false
        var first = true
        while !done {
            guard let block = try handle.read(upToCount: blockSize), block.count == blockSize else { throw FITSError.truncated }
            if first {
                guard block.starts(with: Array("SIMPLE  =".utf8)) else { throw FITSError.notFITS }
                first = false
            }
            done = parseBlock(block, into: &header)
        }
        return header
    }

    /// Parses an in-memory FITS file (primary HDU only).
    public static func read(data: Data) throws -> FITSImage {
        guard data.count >= blockSize, data.starts(with: Array("SIMPLE  =".utf8)) else { throw FITSError.notFITS }
        var header = FITSHeader()
        var offset = 0
        var done = false
        while !done {
            guard offset + blockSize <= data.count else { throw FITSError.truncated }
            done = parseBlock(data.subdata(in: offset..<(offset + blockSize)), into: &header)
            offset += blockSize
        }
        guard let bitpix = header.int("BITPIX"), let naxis = header.int("NAXIS") else { throw FITSError.unsupported("missing BITPIX/NAXIS") }
        guard naxis == 2 || naxis == 3 else { throw FITSError.unsupported("NAXIS = \(naxis)") }
        guard let w = header.int("NAXIS1"), let h = header.int("NAXIS2"), w > 0, h > 0 else { throw FITSError.unsupported("bad dimensions") }
        let nplanes = naxis == 3 ? (header.int("NAXIS3") ?? 1) : 1
        guard nplanes >= 1 && nplanes <= 4 else { throw FITSError.unsupported("NAXIS3 = \(nplanes)") }
        let bzero = header.double("BZERO") ?? 0
        let bscale = header.double("BSCALE") ?? 1
        let bytesPer = abs(bitpix) / 8
        let planeCount = w * h
        let needed = offset + planeCount * nplanes * bytesPer
        guard data.count >= needed else { throw FITSError.truncated }

        var planes: [PlanarImage] = []
        for p in 0..<nplanes {
            let plane = PlanarImage(width: w, height: h)
            let start = offset + p * planeCount * bytesPer
            data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
                let base = raw.baseAddress!.advanced(by: start)
                decode(base: base, count: planeCount, bitpix: bitpix, bzero: bzero, bscale: bscale, into: plane.pointer)
            }
            planes.append(plane)
        }
        // Normalise integer data into [0,1] following the usual astro conventions.
        switch bitpix {
        case 8: planes.forEach { $0.affine(scale: 1 / 255, offset: 0) }
        case 16:
            // Unsigned 16-bit (BZERO = 32768) is the common case: value already in [0,65535].
            // Signed data without BZERO is shifted so the range maps to [0,1] as well.
            let shift: Float = (bzero == 0) ? 32768 : 0
            planes.forEach { $0.affine(scale: 1 / 65535, offset: shift / 65535) }
        case 32:
            let shift: Float = (bzero == 0) ? 2147483648 : 0
            planes.forEach { $0.affine(scale: Float(1.0 / 4294967295.0), offset: Float(Double(shift) / 4294967295.0)) }
        default: break
        }
        if let order = header.string("ROWORDER")?.uppercased(), order == "BOTTOM-UP" {
            planes.forEach { $0.flipVertically() }
        }
        return FITSImage(header: header, planes: planes, bitpix: bitpix)
    }

    /// Returns true when the END card was found in this block.
    private static func parseBlock(_ block: Data, into header: inout FITSHeader) -> Bool {
        var found = false
        block.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            for i in stride(from: 0, to: blockSize, by: 80) {
                let bytes = raw[i..<(i + 80)]
                let s = String(decoding: bytes, as: UTF8.self)
                if s.hasPrefix("END") && s.dropFirst(3).allSatisfy({ $0 == " " }) { found = true; break }
                if let c = FITSHeader.parseCard(s) { header.append(c) }
            }
        }
        return found
    }

    private static func decode(base: UnsafeRawPointer, count: Int, bitpix: Int, bzero: Double, bscale: Double, into out: UnsafeMutablePointer<Float>) {
        let bz = Float(bzero), bs = Float(bscale)
        switch bitpix {
        case -32:
            let p = base.assumingMemoryBound(to: UInt32.self)
            for i in 0..<count { out[i] = Float(bitPattern: UInt32(bigEndian: p[i])) }
        case -64:
            let p = base.assumingMemoryBound(to: UInt64.self)
            for i in 0..<count { out[i] = Float(Double(bitPattern: UInt64(bigEndian: p[i]))) }
        case 16:
            let p = base.assumingMemoryBound(to: UInt16.self)
            for i in 0..<count { out[i] = Float(Int16(bitPattern: UInt16(bigEndian: p[i]))) }
        case 32:
            let p = base.assumingMemoryBound(to: UInt32.self)
            for i in 0..<count { out[i] = Float(Int32(bitPattern: UInt32(bigEndian: p[i]))) }
        case 8:
            let p = base.assumingMemoryBound(to: UInt8.self)
            for i in 0..<count { out[i] = Float(p[i]) }
        case 64:
            let p = base.assumingMemoryBound(to: UInt64.self)
            for i in 0..<count { out[i] = Float(Int64(bitPattern: UInt64(bigEndian: p[i]))) }
        default:
            for i in 0..<count { out[i] = 0 }
        }
        if bs != 1 || bz != 0 {
            var a = bs, b = bz
            vDSP_vsmsa(out, 1, &a, &b, out, 1, vDSP_Length(count))
        }
        // Replace NaN/Inf with 0 so that statistics never break.
        for i in 0..<count where !out[i].isFinite { out[i] = 0 }
    }
}

public enum FITSWriter {
    public enum Depth { case float32, uint16 }

    /// Writes one or three planes as a primary HDU. Rows are written top-down (ROWORDER).
    public static func write(planes: [PlanarImage], to url: URL, depth: Depth = .float32, extraCards: [(String, String, String?)] = []) throws {
        guard let first = planes.first else { throw FITSError.writeFailed("no planes") }
        let w = first.width, h = first.height
        var cards: [String] = []
        func card(_ key: String, _ value: String, _ comment: String? = nil) {
            var s = key.padding(toLength: 8, withPad: " ", startingAt: 0) + "= " + value.leftPadded(to: 20)
            if let c = comment { s += " / " + c }
            cards.append(String(s.prefix(80)).padding(toLength: 80, withPad: " ", startingAt: 0))
        }
        func strCard(_ key: String, _ value: String, _ comment: String? = nil) {
            let v = "'" + value.replacingOccurrences(of: "'", with: "''").padding(toLength: max(8, value.count), withPad: " ", startingAt: 0) + "'"
            var s = key.padding(toLength: 8, withPad: " ", startingAt: 0) + "= " + v
            if let c = comment { s = s.padding(toLength: max(s.count, 30), withPad: " ", startingAt: 0) + " / " + c }
            cards.append(String(s.prefix(80)).padding(toLength: 80, withPad: " ", startingAt: 0))
        }
        card("SIMPLE", "T", "CombineColors FITS")
        card("BITPIX", depth == .float32 ? "-32" : "16", "bits per data element")
        card("NAXIS", planes.count > 1 ? "3" : "2")
        card("NAXIS1", "\(w)")
        card("NAXIS2", "\(h)")
        if planes.count > 1 { card("NAXIS3", "\(planes.count)") }
        if depth == .uint16 {
            card("BZERO", "32768", "offset data range to that of unsigned short")
            card("BSCALE", "1", "default scaling factor")
        } else {
            card("BZERO", "0.0")
            card("BSCALE", "1.0")
        }
        strCard("ROWORDER", "TOP-DOWN", "Order of pixel rows stored in the image array")
        strCard("SOFTWARE", "CombineColors")
        let df = ISO8601DateFormatter(); df.formatOptions = [.withInternetDateTime]
        strCard("DATE", String(df.string(from: Date()).dropLast()), "creation date")
        for (k, v, c) in extraCards {
            if v.hasPrefix("'") { cards.append(String((k.padding(toLength: 8, withPad: " ", startingAt: 0) + "= " + v + (c.map { " / " + $0 } ?? "")).prefix(80)).padding(toLength: 80, withPad: " ", startingAt: 0)) }
            else if Double(v) != nil || v == "T" || v == "F" { card(k, v, c) }
            else { strCard(k, v, c) }
        }
        cards.append("END".padding(toLength: 80, withPad: " ", startingAt: 0))
        var headerString = cards.joined()
        while headerString.utf8.count % FITSReader.blockSize != 0 { headerString += " " }
        var out = Data(headerString.utf8)

        let n = w * h
        if depth == .float32 {
            var buf = [UInt32](repeating: 0, count: n)
            for p in planes {
                for i in 0..<n { buf[i] = p.pointer[i].bitPattern.bigEndian }
                buf.withUnsafeBytes { out.append(contentsOf: $0) }
            }
        } else {
            var buf = [UInt16](repeating: 0, count: n)
            for p in planes {
                for i in 0..<n {
                    let v = max(0, min(65535, (p.pointer[i] * 65535).rounded()))
                    let s = Int32(v) - 32768
                    buf[i] = UInt16(bitPattern: Int16(s)).bigEndian
                }
                buf.withUnsafeBytes { out.append(contentsOf: $0) }
            }
        }
        let pad = (FITSReader.blockSize - out.count % FITSReader.blockSize) % FITSReader.blockSize
        if pad > 0 { out.append(Data(count: pad)) }
        try out.write(to: url, options: .atomic)
    }
}

extension String {
    func leftPadded(to n: Int) -> String {
        count >= n ? self : String(repeating: " ", count: n - count) + self
    }
}
