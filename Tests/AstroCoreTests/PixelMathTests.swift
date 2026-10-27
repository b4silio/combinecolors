import XCTest
@testable import AstroCore

final class PixelMathTests: XCTestCase {
    func eval(_ s: String, _ vars: [String: Float] = [:]) throws -> Float {
        try PixelMath.evaluateScalar(try PixelMath.parse(s), variables: vars)
    }

    func testPrecedence() throws {
        XCTAssertEqual(try eval("1 + 2 * 3"), 7)
        XCTAssertEqual(try eval("(1 + 2) * 3"), 9)
        XCTAssertEqual(try eval("2 ^ 3 ^ 2"), 512)
        XCTAssertEqual(try eval("-2 ^ 2"), -4)
        XCTAssertEqual(try eval("2 ^ -1"), 0.5)
        XCTAssertEqual(try eval("~0.25"), 0.75)
        XCTAssertEqual(try eval("~0.5 ^ 2"), 0.75)     // ~(0.5^2)
        XCTAssertEqual(try eval("0.5 ^ ~0.75"), Float(pow(0.5, 0.25)), accuracy: 1e-6) // 0.5^0.25
        XCTAssertEqual(try eval("7 % 3"), 1)
        XCTAssertEqual(try eval("1 < 2"), 1)
        XCTAssertEqual(try eval("1 > 2 || 3 >= 3"), 1)
        XCTAssertEqual(try eval("1 == 1 && 0"), 0)
        XCTAssertEqual(try eval("!0"), 1)
        XCTAssertEqual(try eval("1 ? 5 : 6"), 5)
        XCTAssertEqual(try eval("0 ? 5 : 6"), 6)
        XCTAssertEqual(try eval("1e-2 * 100"), 1)
        XCTAssertEqual(try eval(".5 + .5"), 1)
        XCTAssertEqual(try eval("pi"), .pi)
    }

    func testFunctions() throws {
        XCTAssertEqual(try eval("min(3, 1, 2)"), 1)
        XCTAssertEqual(try eval("max(3, 1, 2)"), 3)
        XCTAssertEqual(try eval("mean(1, 2, 3)"), 2)
        XCTAssertEqual(try eval("med(5, 1, 3)"), 3)
        XCTAssertEqual(try eval("clamp(2)"), 1)
        XCTAssertEqual(try eval("clamp(-2, -1, 1)"), -1)
        XCTAssertEqual(try eval("iif(1 > 0, 10, 20)"), 10)
        XCTAssertEqual(try eval("lerp(0, 10, 0.3)"), 3, accuracy: 1e-6)
        XCTAssertEqual(try eval("mtf(0.5, 0.3)"), 0.3, accuracy: 1e-6)
        XCTAssertEqual(try eval("mtf(0.25, 0.25)"), 0.5, accuracy: 1e-6)
        XCTAssertEqual(try eval("sqrt(16)"), 4)
        XCTAssertEqual(try eval("pow(2, 10)"), 1024)
        XCTAssertEqual(try eval("abs(-3)"), 3)
        XCTAssertEqual(try eval("step(0.5, 0.7)"), 1)
        XCTAssertEqual(try eval("smoothstep(0, 1, 0.5)"), 0.5, accuracy: 1e-6)
        XCTAssertEqual(try eval("exp(ln(5))"), 5, accuracy: 1e-5)
        XCTAssertEqual(try eval("asinh(0)"), 0)
        XCTAssertEqual(try eval("floor(2.7) + ceil(2.1) + round(2.5)"), 8)
    }

    func testVariablesAndAliases() throws {
        let vars: [String: Float] = ["ha": 0.5, "oiii": 0.25, "sii": 0.125, "r": 1]
        XCTAssertEqual(try eval("Ha + OIII + SII + R", vars), 1.875)
        XCTAssertEqual(try eval("H + O + S", vars), 0.875)
        XCTAssertEqual(try eval("ha*o3*s2", vars), 0.015625)
        XCTAssertEqual(try eval("Halpha", vars), 0.5)
        XCTAssertEqual(try eval("Hydrogen", vars), 0.5)
    }

    func testErrors() {
        XCTAssertThrowsError(try eval("1 +"))
        XCTAssertThrowsError(try eval("(1 + 2"))
        XCTAssertThrowsError(try eval("foo(1)"))
        XCTAssertThrowsError(try eval("min()"))
        XCTAssertThrowsError(try eval("clamp(1, 2)"))
        XCTAssertThrowsError(try eval("1 = 2"))
        XCTAssertThrowsError(try eval(""))
        XCTAssertThrowsError(try eval("Ha + 1"))   // unknown variable in this scope
        XCTAssertThrowsError(try eval("1 # 2"))
        do { _ = try eval("1 + (2 * 3") } catch let e as PixelMathError { XCTAssertEqual(e.message, "Expected ')'"); XCTAssertEqual(e.position, 10) } catch { XCTFail() }
    }

    func testVectorEvaluationMatchesScalar() throws {
        let w = 64, h = 48
        let ha = Synth.channel(width: w, height: h, seed: 1), o3 = Synth.channel(width: w, height: h, seed: 2), s2 = Synth.channel(width: w, height: h, seed: 3)
        for p in [ha, o3, s2] { var r = Stretch.resolve(StretchSettings(), stats: Statistics.compute(image: p)); r.c1 = 1; r.apply(to: p) }
        var env = PixelMathEnvironment(width: w, height: h)
        env.set(.ha, plane: ha, stats: Statistics.compute(image: ha)); env.set(.oiii, plane: o3, stats: Statistics.compute(image: o3)); env.set(.sii, plane: s2)
        let formulas = [
            "(OIII^~OIII)*SII + ~(OIII^~OIII)*Ha",
            "((OIII*Ha)^~(OIII*Ha))*Ha + ~((OIII*Ha)^~(OIII*Ha))*OIII",
            "max(SII,Ha,OIII) * 3*SII/(SII+Ha+OIII+0.001)",
            "iif(Ha > OIII, Ha - OIII, 0) + (SII < 0.5 ? SII : 0.5)",
            "lerp(Ha, OIII, SII) + mtf(0.3, Ha) + clamp(Ha * 2) + min(Ha, 0.2) + med(Ha, OIII, SII)",
            "Ha - cmed(Ha) + cbg(OIII) * 0 + cpct(Ha, 0.5) - cmed(Ha)",
            "sqrt(Ha) + log(1 + OIII) + exp(-SII) + asinh(Ha * 10) + abs(Ha - OIII) + Ha % 0.1 + 2 ^ Ha + Ha ^ OIII",
            "(Ha >= OIII) * 0.5 + (Ha != OIII && SII <= 1) + !(Ha == 2)",
        ]
        for f in formulas {
            let expr = try PixelMath.parse(f)
            let out = try PixelMath.evaluateToPlane(expr, env: env)
            for i in stride(from: 0, to: w * h, by: 97) {
                let vars: [String: Float] = ["ha": ha.pointer[i], "oiii": o3.pointer[i], "sii": s2.pointer[i]]
                let ref = try PixelMath.evaluateScalar(expr, variables: vars, stats: ["ha": env.stats["ha"]!, "oiii": env.stats["oiii"]!])
                XCTAssertEqual(out.pointer[i], ref, accuracy: max(1e-4, abs(ref) * 1e-4), "formula \(f) at \(i)")
            }
        }
    }

    func testProgramCompileAndRequiredChannels() throws {
        let p = PixelMathProgram(red: "SII", green: "Ha", blue: "OIII", luminance: "L")
        let c = try p.compile()
        XCTAssertEqual(c.requiredChannels, [.sii, .ha, .oiii, .luminance])
        XCTAssertTrue(c.unknownIdentifiers.isEmpty)
        let bad = PixelMathProgram(red: "SII +", green: "Ha", blue: "OIII")
        XCTAssertThrowsError(try bad.compile()) { e in XCTAssertTrue((e as! PixelMathError).message.hasPrefix("Red:")) }
        let unknown = try PixelMathProgram(red: "foo", green: "Ha", blue: "OIII").compile()
        XCTAssertEqual(unknown.unknownIdentifiers, ["foo"])
    }

    func testAllPresetPalettesCompile() {
        for p in Palettes.all {
            if case .pixelMath(let prog) = p.kind { XCTAssertNoThrow(try prog.compile(), p.id) }
            XCTAssertFalse(p.requiredChannels.isEmpty, p.id)
        }
        XCTAssertEqual(Set(Palettes.all.map(\.id)).count, Palettes.all.count, "palette ids must be unique")
    }
}
