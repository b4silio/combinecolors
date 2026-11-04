import Foundation
import Accelerate

/// How the data are brightened for StarNet2, whose network was trained on stretched images.
public enum StarRemovalMethod: String, Codable, CaseIterable, Identifiable, Sendable {
    /// StarNet2's own linear mode (`--linear`) with the project's Auto STF values: it stretches each
    /// channel with a midtones transfer function, removes the stars and undoes the stretch exactly.
    case starnetLinear
    /// The project's stretch (whatever method is chosen) applied here, the starless result
    /// un-stretched afterwards by inverting that stretch numerically.
    case appStretch

    public var id: String { rawValue }
    public var displayName: String {
        switch self {
        case .starnetLinear: return "StarNet2 linear mode, with this project's Auto STF values (recommended)"
        case .appStretch: return "This project's stretch, undone afterwards"
        }
    }
}

/// How a file was actually processed.
public enum StarRemovalProcessing: String, Sendable {
    case starnetLinear
    case appStretch
    /// The data already looked stretched: StarNet2 ran on them as they are, and the outputs are stretched too.
    case alreadyStretched

    public var displayName: String {
        switch self {
        case .starnetLinear: return "linear, StarNet2 stretch"
        case .appStretch: return "linear, project stretch"
        case .alreadyStretched: return "already stretched"
        }
    }
}

public struct StarRemovalOptions: Equatable {
    public var method: StarRemovalMethod = .starnetLinear
    /// The project's stretch: Auto STF values for `.starnetLinear`, the whole stretch for `.appStretch`.
    public var stretch = StretchSettings()
    public var upsample = false

    public init(method: StarRemovalMethod = .starnetLinear, stretch: StretchSettings = StretchSettings(), upsample: Bool = false) {
        self.method = method; self.stretch = stretch; self.upsample = upsample
    }
}

public struct StarRemovalResult {
    public let starless: [PlanarImage]
    /// Linear data: original − starless (add it back). Already-stretched data: an unscreen layer (Screen it back).
    public let stars: [PlanarImage]
    public let processing: StarRemovalProcessing
}

/// Files written for one source image.
public struct StarRemovalFileResult: Sendable {
    public let source: URL
    public let starless: URL
    public let stars: URL
    public let processing: StarRemovalProcessing
}

public enum StarRemoval {
    /// Same rule as the app's "already stretched" hint: the sky sits far above a linear level.
    public static func looksStretched(medians: [Float]) -> Bool {
        !medians.isEmpty && medians.reduce(0, +) / Float(medians.count) > 0.08
    }

    /// `<name>_bg.<ext>` and `<name>_stars.<ext>` next to the source (or in `folder`). FITS and XISF
    /// sources give 32-bit float FITS, everything else TIFF.
    public static func outputURLs(for source: URL, folder: URL? = nil) -> (starless: URL, stars: URL) {
        let stem = source.deletingPathExtension().lastPathComponent
        let ext = writesFITS(source) ? "fits" : "tif"
        let dir = folder ?? source.deletingLastPathComponent()
        return (dir.appendingPathComponent("\(stem)_bg.\(ext)"), dir.appendingPathComponent("\(stem)_stars.\(ext)"))
    }

    static func writesFITS(_ source: URL) -> Bool {
        let ext = source.pathExtension.lowercased()
        return ImageLoader.fitsExtensions.contains(ext) || ImageLoader.xisfExtensions.contains(ext)
    }

    /// True for a file this tool wrote (`…_bg`): running it again would only blur the image.
    public static func isStarless(_ url: URL) -> Bool {
        url.deletingPathExtension().lastPathComponent.lowercased().hasSuffix("_bg")
    }

    /// Loads a file, removes its stars (all three planes together for a colour file) and writes
    /// the `_bg` and `_stars` files. The source is never modified.
    public static func processFile(_ url: URL, options: StarRemovalOptions, tool: StarNetTool, folder: URL? = nil,
                                   progress: ((Double) -> Void)? = nil, cancellation: StarNetCancellation? = nil) throws -> StarRemovalFileResult {
        let f = try ImageLoader.load(url: url)
        let planes = f.isColor ? Array(f.planes.prefix(3)) : [f.planes[0]]
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("CombineColors-StarNet-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: work) }
        let r = try run(planes: planes, options: options, tool: tool, workDirectory: work, progress: progress, cancellation: cancellation)
        let out = outputURLs(for: url, folder: folder)
        if writesFITS(url) {
            let cards = copiedCards(f.header)
            try FITSWriter.write(planes: r.starless, to: out.starless, depth: .float32, extraCards: cards + [("IMAGETYP", "'Starless'", "StarNet2 via CombineColors")])
            try FITSWriter.write(planes: r.stars, to: out.stars, depth: .float32, extraCards: cards + [("IMAGETYP", r.processing == .alreadyStretched ? "'Stars (unscreen)'" : "'Stars'", "StarNet2 via CombineColors")])
        } else {
            let format: RasterWriter.Format = r.processing == .alreadyStretched ? .tiff16 : .tiff32
            try RasterWriter.write(planes: r.starless, to: out.starless, format: format)
            try RasterWriter.write(planes: r.stars, to: out.stars, format: format)
        }
        return StarRemovalFileResult(source: url, starless: out.starless, stars: out.stars, processing: r.processing)
    }

    /// Acquisition keywords carried over to the outputs (the filter name keeps channel detection working).
    static func copiedCards(_ header: FITSHeader?) -> [(String, String, String?)] {
        guard let h = header else { return [] }
        let keep: Set<String> = ["FILTER", "OBJECT", "TELESCOP", "INSTRUME", "EXPTIME", "EXPOSURE", "DATE-OBS", "RA", "DEC",
                                 "OBJCTRA", "OBJCTDEC", "FOCALLEN", "XPIXSZ", "YPIXSZ", "XBINNING", "YBINNING", "GAIN", "OFFSET", "CCD-TEMP"]
        var seen = Set<String>()
        return h.cards.compactMap { c in
            guard keep.contains(c.key), let v = c.value, !v.isEmpty, seen.insert(c.key).inserted else { return nil }
            return (c.key, v, c.comment)
        }
    }

    /// Removes the stars of one grey plane or of three RGB planes (values as loaded: [0, 1], or
    /// float ADU up to 65535). The inputs are left untouched.
    public static func run(planes: [PlanarImage], options: StarRemovalOptions, tool: StarNetTool, workDirectory: URL,
                           progress: ((Double) -> Void)? = nil, cancellation: StarNetCancellation? = nil) throws -> StarRemovalResult {
        guard planes.count == 1 || planes.count == 3 else { throw StarNetError.unsupportedPlanes(planes.count) }
        let w = planes[0].width, h = planes[0].height
        let minSide = options.upsample ? StarNet.minimumSize / 2 : StarNet.minimumSize
        guard w >= minSide, h >= minSide else { throw StarNetError.imageTooSmall(w, h) }

        // StarNet2 reads float FITS as normalised [0, 1]: float ADU data are scaled down and back up.
        let maxValue = planes.map { $0.minMax().max }.max() ?? 1
        let scale: Float = maxValue > 1 ? (maxValue <= 65535 ? 65535 : maxValue) : 1
        let input = scale == 1 ? planes : planes.map { p -> PlanarImage in let c = p.copy(); c.affine(scale: 1 / scale, offset: 0); return c }
        let stats = input.map { Statistics.compute(image: $0) }

        var processing: StarRemovalProcessing
        if looksStretched(medians: stats.map(\.median)) { processing = .alreadyStretched }
        else if options.method == .appStretch && options.stretch.method != .none { processing = .appStretch }
        else { processing = .starnetLinear }

        let inURL = workDirectory.appendingPathComponent("input.fits")
        let outURL = workDirectory.appendingPathComponent("starless.fits")
        let starsURL = workDirectory.appendingPathComponent("stars.fits")
        var cmd = StarNetCommand(input: inURL, output: outURL)
        cmd.upsample = options.upsample

        var starless: [PlanarImage]
        var stars: [PlanarImage]
        switch processing {
        case .alreadyStretched:
            let clamped = input.map { p -> PlanarImage in let c = p.copy(); c.clamp(min: 0, max: 1); return c }
            try FITSWriter.write(planes: clamped, to: inURL)
            cmd.unscreen = starsURL
            try tool.run(cmd, progress: progress, cancellation: cancellation)
            starless = try readPlanes(outURL, count: planes.count)
            stars = try readPlanes(starsURL, count: planes.count)
        case .starnetLinear:
            try FITSWriter.write(planes: input, to: inURL)
            cmd.mask = starsURL
            cmd.linear = true
            cmd.shadowsClipping = options.stretch.shadowsClipping
            cmd.targetBackground = options.stretch.targetBackground
            try tool.run(cmd, progress: progress, cancellation: cancellation)
            starless = try readPlanes(outURL, count: planes.count)
            stars = try readPlanes(starsURL, count: planes.count)
        case .appStretch:
            let resolved: [ResolvedStretch] = options.stretch.linked && input.count > 1
                ? Array(repeating: Stretch.resolveLinked(options.stretch, stats: stats), count: input.count)
                : stats.map { Stretch.resolve(options.stretch, stats: $0) }
            let stretched = zip(input, resolved).map { p, r -> PlanarImage in let c = p.copy(); r.apply(to: c); c.clamp(min: 0, max: 1); return c }
            try FITSWriter.write(planes: stretched, to: inURL)
            try tool.run(cmd, progress: progress, cancellation: cancellation)
            starless = try readPlanes(outURL, count: planes.count)
            stars = []
            for (i, r) in resolved.enumerated() {
                InverseStretch(r).apply(to: starless[i])
                restoreClippedShadows(starless: starless[i], original: input[i], blackPoint: r.c0)
                stars.append(difference(input[i], starless[i]))
            }
        }
        if scale != 1 {
            for p in starless { p.affine(scale: scale, offset: 0) }
            if processing != .alreadyStretched { for p in stars { p.affine(scale: scale, offset: 0) } }
        }
        return StarRemovalResult(starless: starless, stars: stars, processing: processing)
    }

    static func readPlanes(_ url: URL, count: Int) throws -> [PlanarImage] {
        guard FileManager.default.fileExists(atPath: url.path) else { throw StarNetError.missingOutput(url.lastPathComponent) }
        let f = try FITSReader.read(url: url)
        guard f.planes.count >= count else { throw StarNetError.missingOutput(url.lastPathComponent) }
        return Array(f.planes.prefix(count))
    }

    /// Samples at or below the stretch's black point all became 0: give them their original value back.
    static func restoreClippedShadows(starless: PlanarImage, original: PlanarImage, blackPoint c0: Float) {
        let s = starless.pointer, o = original.pointer
        for i in 0..<starless.count where o[i] <= c0 { s[i] = o[i] }
    }

    /// max(a − b, 0).
    static func difference(_ a: PlanarImage, _ b: PlanarImage) -> PlanarImage {
        let d = PlanarImage(width: a.width, height: a.height)
        vDSP_vsub(b.pointer, 1, a.pointer, 1, d.pointer, 1, vDSP_Length(a.count))
        d.clamp(min: 0, max: .greatestFiniteMagnitude)
        return d
    }
}

/// Numerical inverse of a resolved stretch: a table of the linear value for evenly spaced
/// stretched values, found by bisection on `ResolvedStretch.evaluate` (every method is monotonic).
/// Stretched values at 0 map to the black point and values at 1 to the white point.
public struct InverseStretch {
    private let table: [Float]
    public let isIdentity: Bool

    public init(_ r: ResolvedStretch, samples: Int = 1 << 18) {
        isIdentity = r.method == .none && (r.gamma == 1 || r.gamma <= 0)
        guard !isIdentity else { table = []; return }
        let lo: Float = r.method == .none ? 0 : r.c0
        let hi: Float = r.method == .none ? 1 : r.c1
        var t = [Float](repeating: 0, count: samples)
        let n = samples
        t.withUnsafeMutableBufferPointer { buf in
            DispatchQueue.concurrentPerform(iterations: 16) { chunk in
                let start = chunk * n / 16, end = (chunk + 1) * n / 16
                for i in start..<end {
                    let y = Float(i) / Float(n - 1)
                    var a = lo, b = hi
                    for _ in 0..<40 {
                        let m = (a + b) / 2
                        if r.evaluate(m) < y { a = m } else { b = m }
                    }
                    buf[i] = (a + b) / 2
                }
            }
        }
        t[0] = lo
        table = t
    }

    public func evaluate(_ y: Float) -> Float {
        if isIdentity { return y }
        let n = table.count
        let pos = min(max(y, 0), 1) * Float(n - 1)
        let i = min(Int(pos), n - 2)
        let f = pos - Float(i)
        return table[i] + f * (table[i + 1] - table[i])
    }

    public func apply(to plane: PlanarImage) {
        guard !isIdentity else { return }
        let p = plane.pointer, count = plane.count
        let chunks = max(1, min(32, count / 65536))
        DispatchQueue.concurrentPerform(iterations: chunks) { c in
            let start = c * count / chunks, end = (c + 1) * count / chunks
            for i in start..<end { p[i] = evaluate(p[i]) }
        }
    }
}
