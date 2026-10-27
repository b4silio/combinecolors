import Foundation
import ImageIO
import CoreGraphics
import Accelerate
import UniformTypeIdentifiers

public enum ImageIOError: Error, LocalizedError {
    case cannotOpen
    case cannotDecode
    case cannotWrite(String)
    case unsupportedFormat(String)

    public var errorDescription: String? {
        switch self {
        case .cannotOpen: return "The image could not be opened."
        case .cannotDecode: return "The image could not be decoded."
        case .cannotWrite(let s): return "The image could not be written: \(s)"
        case .unsupportedFormat(let s): return "Unsupported image format: \(s)"
        }
    }
}

/// Reading TIFF / PNG / JPEG etc. through ImageIO into float planes.
/// Values are returned exactly as stored (integer codes divided by their maximum,
/// floats untouched); no colour-space conversion is applied.
public enum RasterReader {
    public struct Result {
        public let planes: [PlanarImage]   // 1 (gray) or 3 (RGB)
        public let bitsPerComponent: Int
        public let isFloat: Bool
        public let properties: [String: Any]
    }

    public static func read(url: URL) throws -> Result {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { throw ImageIOError.cannotOpen }
        let opts = [kCGImageSourceShouldCache: false, kCGImageSourceShouldAllowFloat: true] as CFDictionary
        guard let img = CGImageSourceCreateImageAtIndex(src, 0, opts) else { throw ImageIOError.cannotDecode }
        let props = (CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [String: Any]) ?? [:]
        return try read(cgImage: img, properties: props)
    }

    public static func read(cgImage img: CGImage, properties: [String: Any] = [:]) throws -> Result {
        let w = img.width, h = img.height
        let isFloat = img.bitmapInfo.contains(.floatComponents)
        let cs = img.colorSpace ?? CGColorSpace(name: CGColorSpace.genericGrayGamma2_2)!
        let components = cs.numberOfComponents
        let channels = components == 1 ? 1 : 3

        guard var srcFormat = vImage_CGImageFormat(cgImage: img) else { throw ImageIOError.cannotDecode }
        let floatInfo = CGBitmapInfo(rawValue: CGBitmapInfo.floatComponents.rawValue | CGImageAlphaInfo.none.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        guard var dstFormat = vImage_CGImageFormat(bitsPerComponent: 32, bitsPerPixel: 32 * channels, colorSpace: cs, bitmapInfo: floatInfo, renderingIntent: .defaultIntent) else { throw ImageIOError.cannotDecode }
        var err = vImage_Error(kvImageNoError)
        guard let conv = vImageConverter_CreateWithCGImageFormat(&srcFormat, &dstFormat, nil, vImage_Flags(kvImageNoFlags), &err)?.takeRetainedValue(), err == kvImageNoError else { throw ImageIOError.cannotDecode }
        var sbuf = vImage_Buffer()
        err = vImageBuffer_InitWithCGImage(&sbuf, &srcFormat, nil, img, vImage_Flags(kvImageNoFlags))
        guard err == kvImageNoError else { throw ImageIOError.cannotDecode }
        defer { free(sbuf.data) }
        var dbuf = vImage_Buffer()
        err = vImageBuffer_Init(&dbuf, vImagePixelCount(h), vImagePixelCount(w), UInt32(32 * channels), vImage_Flags(kvImageNoFlags))
        guard err == kvImageNoError else { throw ImageIOError.cannotDecode }
        defer { free(dbuf.data) }
        err = vImageConvert_AnyToAny(conv, &sbuf, &dbuf, nil, vImage_Flags(kvImageNoFlags))
        guard err == kvImageNoError else { throw ImageIOError.cannotDecode }

        var planes: [PlanarImage] = []
        let rowFloats = dbuf.rowBytes / 4
        let base = dbuf.data.assumingMemoryBound(to: Float.self)
        for c in 0..<channels {
            let plane = PlanarImage(width: w, height: h)
            var one: Float = 1, zero: Float = 0
            for y in 0..<h {
                vDSP_vsmsa(base + y * rowFloats + c, vDSP_Stride(channels), &one, &zero, plane.pointer + y * w, 1, vDSP_Length(w))
            }
            planes.append(plane)
        }
        for p in planes {
            for i in 0..<p.count where !p.pointer[i].isFinite { p.pointer[i] = 0 }
        }
        return Result(planes: planes, bitsPerComponent: img.bitsPerComponent, isFloat: isFloat, properties: properties)
    }
}

/// Export helpers.
public enum RasterWriter {
    public enum Format: String, CaseIterable, Codable, Identifiable {
        case tiff16 = "TIFF 16-bit"
        case tiff32 = "TIFF 32-bit float"
        case png16 = "PNG 16-bit"
        case png8 = "PNG 8-bit"
        case jpeg = "JPEG"
        case fits32 = "FITS 32-bit float"
        case fits16 = "FITS 16-bit"

        public var id: String { rawValue }
        public var fileExtension: String {
            switch self {
            case .tiff16, .tiff32: return "tif"
            case .png16, .png8: return "png"
            case .jpeg: return "jpg"
            case .fits32, .fits16: return "fits"
            }
        }
        public var utType: UTType {
            switch self {
            case .tiff16, .tiff32: return .tiff
            case .png16, .png8: return .png
            case .jpeg: return .jpeg
            case .fits32, .fits16: return UTType("public.fits") ?? .data
            }
        }
        public var isFITS: Bool { self == .fits32 || self == .fits16 }
    }

    /// Builds a CGImage from 1 or 3 float planes with the requested depth.
    /// Values are clamped to [0,1]. `sRGB` tagging keeps Finder/Preview rendering consistent.
    public static func makeCGImage(planes: [PlanarImage], bitsPerComponent: Int, float: Bool = false) throws -> CGImage {
        let w = planes[0].width, h = planes[0].height
        let channels = planes.count == 1 ? 1 : 3
        let cs = channels == 1 ? CGColorSpace(name: CGColorSpace.genericGrayGamma2_2)! : CGColorSpace(name: CGColorSpace.sRGB)!
        let n = w * h
        var data = Data()
        var bitmapInfo: CGBitmapInfo
        if float {
            bitmapInfo = CGBitmapInfo(rawValue: CGBitmapInfo.floatComponents.rawValue | CGImageAlphaInfo.none.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
            var inter = [Float](repeating: 0, count: n * channels)
            for c in 0..<channels {
                let p = planes[c]
                inter.withUnsafeMutableBufferPointer { buf in
                    var one: Float = 1, zero: Float = 0
                    vDSP_vsmsa(p.pointer, 1, &one, &zero, buf.baseAddress! + c, vDSP_Stride(channels), vDSP_Length(n))
                }
            }
            inter.withUnsafeBytes { data.append(contentsOf: $0) }
        } else if bitsPerComponent == 16 {
            bitmapInfo = CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue | CGBitmapInfo.byteOrder16Big.rawValue)
            var inter = [UInt16](repeating: 0, count: n * channels)
            let tmp = UnsafeMutablePointer<Float>.allocate(capacity: n)
            defer { tmp.deallocate() }
            for c in 0..<channels {
                let p = planes[c]
                var lo: Float = 0, hi: Float = 1
                vDSP_vclip(p.pointer, 1, &lo, &hi, tmp, 1, vDSP_Length(n))
                var s: Float = 65535
                vDSP_vsmul(tmp, 1, &s, tmp, 1, vDSP_Length(n))
                inter.withUnsafeMutableBufferPointer { buf in
                    vDSP_vfixru16(tmp, 1, buf.baseAddress! + c, vDSP_Stride(channels), vDSP_Length(n))
                }
            }
            for i in 0..<inter.count { inter[i] = inter[i].bigEndian }
            inter.withUnsafeBytes { data.append(contentsOf: $0) }
        } else {
            bitmapInfo = CGBitmapInfo(rawValue: CGImageAlphaInfo.none.rawValue)
            var inter = [UInt8](repeating: 0, count: n * channels)
            let tmp = UnsafeMutablePointer<Float>.allocate(capacity: n)
            defer { tmp.deallocate() }
            for c in 0..<channels {
                let p = planes[c]
                var lo: Float = 0, hi: Float = 1
                vDSP_vclip(p.pointer, 1, &lo, &hi, tmp, 1, vDSP_Length(n))
                var s: Float = 255
                vDSP_vsmul(tmp, 1, &s, tmp, 1, vDSP_Length(n))
                inter.withUnsafeMutableBufferPointer { buf in
                    vDSP_vfixru8(tmp, 1, buf.baseAddress! + c, vDSP_Stride(channels), vDSP_Length(n))
                }
            }
            inter.withUnsafeBytes { data.append(contentsOf: $0) }
        }
        let bpc = float ? 32 : bitsPerComponent
        let bpp = bpc * channels
        guard let provider = CGDataProvider(data: data as CFData),
              let img = CGImage(width: w, height: h, bitsPerComponent: bpc, bitsPerPixel: bpp, bytesPerRow: w * bpp / 8, space: cs, bitmapInfo: bitmapInfo, provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent) else {
            throw ImageIOError.cannotWrite("CGImage creation failed")
        }
        return img
    }

    /// Fast 8-bit RGBA (display) image from float planes already in [0,1].
    public static func makeDisplayImage(rgb: RGBImage) -> CGImage? {
        let w = rgb.width, h = rgb.height, n = w * h
        let bytes = UnsafeMutablePointer<UInt8>.allocate(capacity: n * 4)
        let tmp = UnsafeMutablePointer<Float>.allocate(capacity: n)
        defer { tmp.deallocate() }
        for (c, p) in rgb.planes.enumerated() {
            var lo: Float = 0, hi: Float = 255
            var s: Float = 255
            vDSP_vsmul(p.pointer, 1, &s, tmp, 1, vDSP_Length(n))
            vDSP_vclip(tmp, 1, &lo, &hi, tmp, 1, vDSP_Length(n))
            vDSP_vfixru8(tmp, 1, bytes + c, 4, vDSP_Length(n))
        }
        var ff: Float = 255
        vDSP_vfill(&ff, tmp, 1, vDSP_Length(n))
        vDSP_vfixru8(tmp, 1, bytes + 3, 4, vDSP_Length(n))
        let data = Data(bytesNoCopy: bytes, count: n * 4, deallocator: .custom { p, _ in p.deallocate() })
        guard let provider = CGDataProvider(data: data as CFData) else { return nil }
        return CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue), provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }

    public static func makeDisplayImage(gray: PlanarImage) -> CGImage? {
        makeDisplayImage(rgb: RGBImage(r: gray, g: gray, b: gray))
    }

    public static func write(planes: [PlanarImage], to url: URL, format: Format, jpegQuality: Double = 0.92) throws {
        switch format {
        case .fits32: try FITSWriter.write(planes: planes, to: url, depth: .float32)
        case .fits16: try FITSWriter.write(planes: planes, to: url, depth: .uint16)
        default:
            let img: CGImage
            switch format {
            case .tiff16, .png16: img = try makeCGImage(planes: planes, bitsPerComponent: 16)
            case .tiff32: img = try makeCGImage(planes: planes, bitsPerComponent: 32, float: true)
            default: img = try makeCGImage(planes: planes, bitsPerComponent: 8)
            }
            guard let dest = CGImageDestinationCreateWithURL(url as CFURL, format.utType.identifier as CFString, 1, nil) else { throw ImageIOError.cannotWrite("destination") }
            var props: [CFString: Any] = [:]
            if format == .jpeg { props[kCGImageDestinationLossyCompressionQuality] = jpegQuality }
            if format == .tiff16 || format == .tiff32 { props[kCGImagePropertyTIFFDictionary] = [kCGImagePropertyTIFFCompression: 1] }
            CGImageDestinationAddImage(dest, img, props as CFDictionary)
            guard CGImageDestinationFinalize(dest) else { throw ImageIOError.cannotWrite("finalize") }
        }
    }
}
