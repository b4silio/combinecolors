import Foundation

/// A file loaded from disk, before it is assigned to channel slots.
public struct LoadedFile {
    public let url: URL
    public let planes: [PlanarImage]
    public let header: FITSHeader?
    public let filterName: String?
    public let detectedKind: ChannelKind?
    public let formatDescription: String

    public var width: Int { planes[0].width }
    public var height: Int { planes[0].height }
    public var isColor: Bool { planes.count >= 3 }
    public var fileName: String { url.lastPathComponent }
}

public enum ImageLoader {
    public static let fitsExtensions: Set<String> = ["fits", "fit", "fts"]
    public static let xisfExtensions: Set<String> = ["xisf"]
    public static let rasterExtensions: Set<String> = ["tif", "tiff", "png", "jpg", "jpeg", "heic"]
    public static var allExtensions: Set<String> { fitsExtensions.union(xisfExtensions).union(rasterExtensions) }

    public static func isSupported(_ url: URL) -> Bool {
        allExtensions.contains(url.pathExtension.lowercased())
    }

    public static func load(url: URL) throws -> LoadedFile {
        let ext = url.pathExtension.lowercased()
        if fitsExtensions.contains(ext) {
            let f = try FITSReader.read(url: url)
            let filter = f.header.string("FILTER") ?? f.header.string("FILT-1")
            let kind = f.planes.count == 1 ? ChannelDetection.detect(fileName: url.lastPathComponent, filterKeyword: filter) : nil
            let depth: String
            switch f.bitpix {
            case -32: depth = "32-bit float"
            case -64: depth = "64-bit float"
            case 16: depth = "16-bit integer"
            case 32: depth = "32-bit integer"
            case 8: depth = "8-bit"
            default: depth = "BITPIX \(f.bitpix)"
            }
            return LoadedFile(url: url, planes: f.planes, header: f.header, filterName: filter, detectedKind: kind,
                              formatDescription: "FITS \(depth)\(f.planes.count > 1 ? " ×\(f.planes.count)" : "")")
        }
        if xisfExtensions.contains(ext) {
            let x = try XISFReader.read(url: url)
            let filter = XISFReader.filterName(x)
            let kind = x.planes.count == 1 ? ChannelDetection.detect(fileName: url.lastPathComponent, filterKeyword: filter) : nil
            let depth: String
            switch x.sampleFormat {
            case "Float32": depth = "32-bit float"
            case "Float64": depth = "64-bit float"
            case "UInt8": depth = "8-bit"
            default: depth = x.sampleFormat.replacingOccurrences(of: "UInt", with: "") + "-bit integer"
            }
            return LoadedFile(url: url, planes: x.planes, header: x.header, filterName: filter, detectedKind: kind,
                              formatDescription: "XISF \(depth)\(x.planes.count > 1 ? " ×\(x.planes.count)" : "")\(x.compression.map { " (\($0))" } ?? "")")
        }
        let r = try RasterReader.read(url: url)
        let kind = r.planes.count == 1 ? ChannelDetection.detect(fileName: url.lastPathComponent) : nil
        let depth = r.isFloat ? "32-bit float" : "\(r.bitsPerComponent)-bit"
        return LoadedFile(url: url, planes: r.planes, header: nil, filterName: nil, detectedKind: kind,
                          formatDescription: "\(ext.uppercased()) \(depth)\(r.planes.count > 1 ? " RGB" : "")")
    }
}
