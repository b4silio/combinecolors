import Foundation
import Accelerate

/// Builds a binary exclusion mask (1 = star / invalid, 0 = usable sky or nebula) so
/// that statistics describe the background and the extended emission rather than stars.
public enum StarMask {
    public struct Options {
        /// Detection threshold in units of the high-pass noise sigma.
        public var thresholdSigma: Float = 4
        /// Half-size of the dilation applied to detected pixels (in mask pixels).
        public var dilation: Int = 2
        /// Pyramid levels used to estimate the local background (2^n blur radius).
        public var backgroundLevels: Int = 3
        /// Upper bound on the pixels sampled for the noise estimate.
        public var maxSamples: Int = 300_000
        public init() {}
    }

    /// Builds the mask for `image` (normally a pyramid level with a few megapixels).
    public static func build(image: PlanarImage, options: Options = Options()) -> PlanarImage {
        let w = image.width, h = image.height
        // Local background: heavy box-pyramid blur then bilinear up-scaling.
        var bg = image
        for _ in 0..<options.backgroundLevels where bg.width > 8 && bg.height > 8 { bg = bg.downsampled2x() }
        let background = bg === image ? image.copy() : bg.resized(width: w, height: h)
        // High-pass residual.
        let hp = PlanarImage(width: w, height: h)
        vDSP_vsub(background.pointer, 1, image.pointer, 1, hp.pointer, 1, vDSP_Length(w * h)) // hp = image - background
        // Robust sigma of the residual on a sample excluding zero padding.
        var sampleHP: [Float] = []
        sampleHP.reserveCapacity(min(w * h, options.maxSamples))
        let step = max(1, (w * h) / options.maxSamples)
        var i = 0
        while i < w * h { if image.pointer[i] != 0 { sampleHP.append(hp.pointer[i]) }; i += step }
        var sigma: Float = 0
        if !sampleHP.isEmpty {
            sampleHP.withUnsafeMutableBufferPointer { vDSP_vsort($0.baseAddress!, vDSP_Length($0.count), 1) }
            let med = Statistics.percentile(sorted: sampleHP, q: 0.5)
            sigma = 1.4826 * Statistics.medianAbsoluteDeviation(sorted: sampleHP, center: med)
        }
        let thr = max(sigma * options.thresholdSigma, 1e-12)
        // Threshold: 1 where hp > thr.
        let mask = PlanarImage(width: w, height: h)
        var t = thr
        // vDSP_vthrsc: out = (in > thr) ? C : -C ; use C = 0.5 then add 0.5 → {0,1}
        var c: Float = 0.5
        vDSP_vthrsc(hp.pointer, 1, &t, &c, mask.pointer, 1, vDSP_Length(w * h))
        var half: Float = 0.5
        vDSP_vsadd(mask.pointer, 1, &half, mask.pointer, 1, vDSP_Length(w * h))
        // Dilate.
        if options.dilation > 0 {
            let k = 2 * options.dilation + 1
            let dilated = PlanarImage(width: w, height: h)
            var src = vImage_Buffer(data: mask.pointer, height: vImagePixelCount(h), width: vImagePixelCount(w), rowBytes: w * 4)
            var dst = vImage_Buffer(data: dilated.pointer, height: vImagePixelCount(h), width: vImagePixelCount(w), rowBytes: w * 4)
            vImageMax_PlanarF(&src, &dst, nil, 0, 0, vImagePixelCount(k), vImagePixelCount(k), vImage_Flags(kvImageEdgeExtend))
            mask.pointer.update(from: dilated.pointer, count: w * h)
        }
        // Invalid (zero) pixels are excluded as well.
        for j in 0..<(w * h) where image.pointer[j] == 0 { mask.pointer[j] = 1 }
        return mask
    }

    /// Fraction of masked pixels.
    public static func coverage(_ mask: PlanarImage) -> Float {
        var s: Float = 0
        vDSP_sve(mask.pointer, 1, &s, vDSP_Length(mask.count))
        return s / Float(mask.count)
    }
}
