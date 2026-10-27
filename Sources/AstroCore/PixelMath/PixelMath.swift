import Foundation
import Accelerate

// MARK: - Errors

public struct PixelMathError: Error, LocalizedError, Equatable {
    public let message: String
    /// Character offset in the source string.
    public let position: Int

    public var errorDescription: String? { "\(message) (at \(position))" }
}

// MARK: - Lexer

enum Token: Equatable {
    case number(Float)
    case identifier(String)
    case op(String)       // + - * / % ^ ~ ! < > <= >= == != && || ? :
    case lparen, rparen, comma
    case end
}

struct Lexer {
    let chars: [Character]
    var pos = 0

    init(_ s: String) { chars = Array(s) }

    mutating func tokens() throws -> [(Token, Int)] {
        var out: [(Token, Int)] = []
        while true {
            let (t, p) = try next()
            out.append((t, p))
            if t == .end { return out }
        }
    }

    mutating func next() throws -> (Token, Int) {
        while pos < chars.count, chars[pos].isWhitespace { pos += 1 }
        guard pos < chars.count else { return (.end, pos) }
        let start = pos
        let c = chars[pos]
        if c.isNumber || (c == "." && pos + 1 < chars.count && chars[pos + 1].isNumber) {
            var s = ""
            while pos < chars.count, chars[pos].isNumber || chars[pos] == "." { s.append(chars[pos]); pos += 1 }
            if pos < chars.count, chars[pos] == "e" || chars[pos] == "E" {
                var look = pos + 1
                var exp = "e"
                if look < chars.count, chars[look] == "+" || chars[look] == "-" { exp.append(chars[look]); look += 1 }
                if look < chars.count, chars[look].isNumber {
                    while look < chars.count, chars[look].isNumber { exp.append(chars[look]); look += 1 }
                    s += exp; pos = look
                }
            }
            guard let v = Float(s) else { throw PixelMathError(message: "Invalid number '\(s)'", position: start) }
            return (.number(v), start)
        }
        if c.isLetter || c == "_" || c == "$" {
            var s = ""
            while pos < chars.count, chars[pos].isLetter || chars[pos].isNumber || chars[pos] == "_" || chars[pos] == "$" { s.append(chars[pos]); pos += 1 }
            return (.identifier(s), start)
        }
        pos += 1
        switch c {
        case "(": return (.lparen, start)
        case ")": return (.rparen, start)
        case ",": return (.comma, start)
        case "+", "-", "*", "/", "%", "^", "~", "?", ":": return (.op(String(c)), start)
        case "<", ">", "=", "!":
            if pos < chars.count, chars[pos] == "=" { pos += 1; return (.op(String(c) + "="), start) }
            if c == "=" { throw PixelMathError(message: "Use '==' for comparison", position: start) }
            return (.op(String(c)), start)
        case "&":
            if pos < chars.count, chars[pos] == "&" { pos += 1 }
            return (.op("&&"), start)
        case "|":
            if pos < chars.count, chars[pos] == "|" { pos += 1 }
            return (.op("||"), start)
        default:
            throw PixelMathError(message: "Unexpected character '\(c)'", position: start)
        }
    }
}

// MARK: - AST

public indirect enum Expr: Equatable {
    case number(Float)
    case variable(String)
    case unary(String, Expr)
    case binary(String, Expr, Expr)
    case call(String, [Expr])
    case ternary(Expr, Expr, Expr)

    /// Every identifier referenced (lower-cased), excluding constants.
    public var variables: Set<String> {
        switch self {
        case .number: return []
        case .variable(let v): return PixelMath.constants[v.lowercased()] == nil ? [v.lowercased()] : []
        case .unary(_, let e): return e.variables
        case .binary(_, let a, let b): return a.variables.union(b.variables)
        case .call(_, let args): return args.reduce(into: Set<String>()) { $0.formUnion($1.variables) }
        case .ternary(let c, let a, let b): return c.variables.union(a.variables).union(b.variables)
        }
    }
}

// MARK: - Parser

struct Parser {
    let tokens: [(Token, Int)]
    var i = 0

    init(_ source: String) throws {
        var lx = Lexer(source)
        tokens = try lx.tokens()
    }

    var current: Token { tokens[i].0 }
    var position: Int { tokens[i].1 }

    mutating func parseProgram() throws -> Expr {
        if current == .end { throw PixelMathError(message: "Empty expression", position: 0) }
        let e = try parseTernary()
        guard current == .end else { throw PixelMathError(message: "Unexpected token", position: position) }
        return e
    }

    mutating func parseTernary() throws -> Expr {
        let c = try parseOr()
        if current == .op("?") {
            i += 1
            let a = try parseTernary()
            guard current == .op(":") else { throw PixelMathError(message: "Expected ':'", position: position) }
            i += 1
            let b = try parseTernary()
            return .ternary(c, a, b)
        }
        return c
    }

    mutating func parseOr() throws -> Expr {
        var l = try parseAnd()
        while current == .op("||") { i += 1; l = .binary("||", l, try parseAnd()) }
        return l
    }

    mutating func parseAnd() throws -> Expr {
        var l = try parseEquality()
        while current == .op("&&") { i += 1; l = .binary("&&", l, try parseEquality()) }
        return l
    }

    mutating func parseEquality() throws -> Expr {
        var l = try parseComparison()
        while case .op(let o) = current, o == "==" || o == "!=" { i += 1; l = .binary(o, l, try parseComparison()) }
        return l
    }

    mutating func parseComparison() throws -> Expr {
        var l = try parseAdditive()
        while case .op(let o) = current, ["<", ">", "<=", ">="].contains(o) { i += 1; l = .binary(o, l, try parseAdditive()) }
        return l
    }

    mutating func parseAdditive() throws -> Expr {
        var l = try parseMultiplicative()
        while case .op(let o) = current, o == "+" || o == "-" { i += 1; l = .binary(o, l, try parseMultiplicative()) }
        return l
    }

    mutating func parseMultiplicative() throws -> Expr {
        var l = try parseUnary()
        while case .op(let o) = current, o == "*" || o == "/" || o == "%" { i += 1; l = .binary(o, l, try parseUnary()) }
        return l
    }

    mutating func parseUnary() throws -> Expr {
        if case .op(let o) = current, o == "-" || o == "~" || o == "!" {
            i += 1
            return .unary(o, try parseUnary())
        }
        if current == .op("+") { i += 1; return try parseUnary() }
        return try parsePower()
    }

    mutating func parsePower() throws -> Expr {
        let base = try parsePrimary()
        if current == .op("^") {
            i += 1
            let exp = try parseUnary()   // right associative, allows ~x / -x exponents
            return .binary("^", base, exp)
        }
        return base
    }

    mutating func parsePrimary() throws -> Expr {
        let p = position
        switch current {
        case .number(let v):
            i += 1
            return .number(v)
        case .identifier(let name):
            i += 1
            if current == .lparen {
                i += 1
                var args: [Expr] = []
                if current != .rparen {
                    while true {
                        args.append(try parseTernary())
                        if current == .comma { i += 1; continue }
                        break
                    }
                }
                guard current == .rparen else { throw PixelMathError(message: "Expected ')'", position: position) }
                i += 1
                guard PixelMath.functionArity[name.lowercased()] != nil else { throw PixelMathError(message: "Unknown function '\(name)'", position: p) }
                let arity = PixelMath.functionArity[name.lowercased()]!
                if let fixed = arity.fixed, !fixed.contains(args.count) {
                    throw PixelMathError(message: "'\(name)' expects \(fixed.map(String.init).joined(separator: " or ")) argument(s)", position: p)
                }
                if let minA = arity.min, args.count < minA {
                    throw PixelMathError(message: "'\(name)' expects at least \(minA) arguments", position: p)
                }
                return .call(name.lowercased(), args)
            }
            return .variable(name)
        case .lparen:
            i += 1
            let e = try parseTernary()
            guard current == .rparen else { throw PixelMathError(message: "Expected ')'", position: position) }
            i += 1
            return e
        case .rparen: throw PixelMathError(message: "Unexpected ')'", position: p)
        case .comma: throw PixelMathError(message: "Unexpected ','", position: p)
        case .op(let o): throw PixelMathError(message: "Unexpected operator '\(o)'", position: p)
        case .end: throw PixelMathError(message: "Unexpected end of expression", position: p)
        }
    }
}

// MARK: - Values and evaluation environment

public enum PMValue {
    case scalar(Float)
    case plane(PlanarImage)
}

/// Variables available to an expression.
public struct PixelMathEnvironment {
    public var width: Int
    public var height: Int
    /// Planes keyed by canonical lower-case identifiers (all aliases registered).
    public var planes: [String: PlanarImage] = [:]
    public var stats: [String: ChannelStats] = [:]

    public init(width: Int, height: Int) { self.width = width; self.height = height }

    public mutating func set(_ kind: ChannelKind, plane: PlanarImage, stats: ChannelStats? = nil) {
        for id in kind.pixelMathIdentifiers {
            planes[id] = plane
            if let s = stats { self.stats[id] = s }
        }
    }

    public mutating func set(name: String, plane: PlanarImage, stats: ChannelStats? = nil) {
        planes[name.lowercased()] = plane
        if let s = stats { self.stats[name.lowercased()] = s }
    }
}

public enum PixelMath {
    public static let constants: [String: Float] = ["pi": .pi, "e": Float(M_E)]

    struct Arity { let fixed: [Int]?; let min: Int? }
    static let functionArity: [String: Arity] = [
        "abs": .init(fixed: [1], min: nil), "sqrt": .init(fixed: [1], min: nil), "exp": .init(fixed: [1], min: nil),
        "ln": .init(fixed: [1], min: nil), "log": .init(fixed: [1], min: nil), "log10": .init(fixed: [1], min: nil), "log2": .init(fixed: [1], min: nil),
        "sin": .init(fixed: [1], min: nil), "cos": .init(fixed: [1], min: nil), "tan": .init(fixed: [1], min: nil),
        "asin": .init(fixed: [1], min: nil), "acos": .init(fixed: [1], min: nil), "atan": .init(fixed: [1], min: nil),
        "sinh": .init(fixed: [1], min: nil), "cosh": .init(fixed: [1], min: nil), "tanh": .init(fixed: [1], min: nil), "asinh": .init(fixed: [1], min: nil),
        "sign": .init(fixed: [1], min: nil), "floor": .init(fixed: [1], min: nil), "ceil": .init(fixed: [1], min: nil), "round": .init(fixed: [1], min: nil), "frac": .init(fixed: [1], min: nil),
        "pow": .init(fixed: [2], min: nil), "min": .init(fixed: nil, min: 1), "max": .init(fixed: nil, min: 1),
        "mean": .init(fixed: nil, min: 1), "avg": .init(fixed: nil, min: 1), "med": .init(fixed: nil, min: 1),
        "clamp": .init(fixed: [1, 3], min: nil), "iif": .init(fixed: [3], min: nil), "lerp": .init(fixed: [3], min: nil), "mix": .init(fixed: [3], min: nil),
        "mtf": .init(fixed: [2], min: nil), "step": .init(fixed: [2], min: nil), "smoothstep": .init(fixed: [3], min: nil),
        "cmed": .init(fixed: [1], min: nil), "cmean": .init(fixed: [1], min: nil), "cmin": .init(fixed: [1], min: nil), "cmax": .init(fixed: [1], min: nil),
        "cmad": .init(fixed: [1], min: nil), "cbg": .init(fixed: [1], min: nil), "cnoise": .init(fixed: [1], min: nil), "cpct": .init(fixed: [2], min: nil),
    ]

    /// Human readable help, shown in the Pixel Math editor.
    public static let help: [(String, String)] = [
        ("Operators", "+ - * / % ^   ~x = 1 - x   -x   !x   comparisons < > <= >= == !=   && ||   c ? a : b"),
        ("Channels", "Ha (H, Halpha), OIII (O, O3), SII (S, S2), Hb, R, G, B, L — case-insensitive"),
        ("Math", "abs sqrt exp ln log10 log2 pow(x,y) sin cos tan asin acos atan sinh cosh tanh asinh sign floor ceil round frac"),
        ("Pixel ops", "min(a,b,…) max(a,b,…) mean(a,b,…) med(a,b,…) clamp(x[,lo,hi]) iif(cond,a,b) lerp(a,b,t) step(edge,x) smoothstep(e0,e1,x) mtf(m,x)"),
        ("Channel stats", "cmed(X) cmean(X) cmin(X) cmax(X) cmad(X) cbg(X) cnoise(X) cpct(X, q) with q in 0…1 — scalars from the whole channel"),
        ("Constants", "pi, e"),
    ]

    public static func parse(_ source: String) throws -> Expr {
        var p = try Parser(source)
        return try p.parseProgram()
    }

    // MARK: Evaluation

    public static func evaluate(_ source: String, env: PixelMathEnvironment) throws -> PlanarImage {
        let e = try parse(source)
        return try evaluateToPlane(e, env: env)
    }

    public static func evaluateToPlane(_ e: Expr, env: PixelMathEnvironment) throws -> PlanarImage {
        switch try evaluate(e, env: env) {
        case .plane(let p): return p
        case .scalar(let s): return PlanarImage(width: env.width, height: env.height, fill: s)
        }
    }

    /// Scalar reference evaluation for one pixel (used by tests / read-outs).
    public static func evaluateScalar(_ e: Expr, variables: [String: Float], stats: [String: ChannelStats] = [:]) throws -> Float {
        var env = PixelMathEnvironment(width: 1, height: 1)
        for (k, v) in variables {
            let plane = PlanarImage(width: 1, height: 1, fill: v)
            if let kind = ChannelKind.fromPixelMathIdentifier(k) { env.set(kind, plane: plane, stats: stats[k.lowercased()]) }
            else { env.set(name: k, plane: plane, stats: stats[k.lowercased()]) }
        }
        for (k, s) in stats { env.stats[k.lowercased()] = s }
        let r = try evaluate(e, env: env)
        switch r {
        case .scalar(let s): return s
        case .plane(let p): return p.pointer[0]
        }
    }

    public static func evaluate(_ e: Expr, env: PixelMathEnvironment) throws -> PMValue {
        switch e {
        case .number(let v): return .scalar(v)
        case .variable(let name):
            let key = name.lowercased()
            if let c = constants[key] { return .scalar(c) }
            if let p = env.planes[key] { return .plane(p) }
            throw PixelMathError(message: "Unknown variable '\(name)'", position: 0)
        case .unary(let op, let a):
            let v = try evaluate(a, env: env)
            switch op {
            case "-": return map(v, env) { -$0 } vector: { p, n in var m: Float = -1; vDSP_vsmul(p, 1, &m, p, 1, n) }
            case "~": return map(v, env) { 1 - $0 } vector: { p, n in var m: Float = -1, o: Float = 1; vDSP_vsmsa(p, 1, &m, &o, p, 1, n) }
            case "!": return map(v, env) { $0 == 0 ? 1 : 0 } vector: { p, n in for i in 0..<Int(n) { p[i] = p[i] == 0 ? 1 : 0 } }
            default: throw PixelMathError(message: "Unknown unary operator \(op)", position: 0)
            }
        case .binary(let op, let a, let b):
            let va = try evaluate(a, env: env), vb = try evaluate(b, env: env)
            return try binary(op, va, vb, env)
        case .ternary(let c, let a, let b):
            let vc = try evaluate(c, env: env)
            if case .scalar(let s) = vc { return try evaluate(s != 0 ? a : b, env: env) }
            let va = try evaluate(a, env: env), vb = try evaluate(b, env: env)
            return select(vc, va, vb, env)
        case .call(let name, let args):
            return try call(name, args, env)
        }
    }

    // MARK: Helpers

    private static func materialize(_ v: PMValue, _ env: PixelMathEnvironment) -> PlanarImage {
        switch v {
        case .plane(let p): return p.copy()
        case .scalar(let s): return PlanarImage(width: env.width, height: env.height, fill: s)
        }
    }

    private static func map(_ v: PMValue, _ env: PixelMathEnvironment, scalar: (Float) -> Float, vector: (UnsafeMutablePointer<Float>, vDSP_Length) -> Void) -> PMValue {
        switch v {
        case .scalar(let s): return .scalar(scalar(s))
        case .plane(let p):
            let out = p.copy()
            vector(out.pointer, vDSP_Length(out.count))
            return .plane(out)
        }
    }

    private static func select(_ c: PMValue, _ a: PMValue, _ b: PMValue, _ env: PixelMathEnvironment) -> PMValue {
        let cp = materialize(c, env), ap = materialize(a, env), bp = materialize(b, env)
        for i in 0..<cp.count { cp.pointer[i] = cp.pointer[i] != 0 ? ap.pointer[i] : bp.pointer[i] }
        return .plane(cp)
    }

    private static func binary(_ op: String, _ a: PMValue, _ b: PMValue, _ env: PixelMathEnvironment) throws -> PMValue {
        if case .scalar(let x) = a, case .scalar(let y) = b {
            return .scalar(try scalarBinary(op, x, y))
        }
        let n = vDSP_Length(env.width * env.height)
        switch op {
        case "+":
            if case .scalar(var s) = a, case .plane(let p) = b { let o = p.copy(); vDSP_vsadd(o.pointer, 1, &s, o.pointer, 1, n); return .plane(o) }
            if case .scalar(var s) = b, case .plane(let p) = a { let o = p.copy(); vDSP_vsadd(o.pointer, 1, &s, o.pointer, 1, n); return .plane(o) }
            if case .plane(let p) = a, case .plane(let q) = b { let o = PlanarImage(width: p.width, height: p.height); vDSP_vadd(p.pointer, 1, q.pointer, 1, o.pointer, 1, n); return .plane(o) }
        case "-":
            if case .scalar(let s) = a, case .plane(let p) = b { let o = p.copy(); var m: Float = -1, ss = s; vDSP_vsmsa(o.pointer, 1, &m, &ss, o.pointer, 1, n); return .plane(o) }
            if case .scalar(let s) = b, case .plane(let p) = a { let o = p.copy(); var ns = -s; vDSP_vsadd(o.pointer, 1, &ns, o.pointer, 1, n); return .plane(o) }
            if case .plane(let p) = a, case .plane(let q) = b { let o = PlanarImage(width: p.width, height: p.height); vDSP_vsub(q.pointer, 1, p.pointer, 1, o.pointer, 1, n); return .plane(o) }
        case "*":
            if case .scalar(var s) = a, case .plane(let p) = b { let o = p.copy(); vDSP_vsmul(o.pointer, 1, &s, o.pointer, 1, n); return .plane(o) }
            if case .scalar(var s) = b, case .plane(let p) = a { let o = p.copy(); vDSP_vsmul(o.pointer, 1, &s, o.pointer, 1, n); return .plane(o) }
            if case .plane(let p) = a, case .plane(let q) = b { let o = PlanarImage(width: p.width, height: p.height); vDSP_vmul(p.pointer, 1, q.pointer, 1, o.pointer, 1, n); return .plane(o) }
        case "/":
            if case .scalar(var s) = a, case .plane(let p) = b { let o = p.copy(); vDSP_svdiv(&s, p.pointer, 1, o.pointer, 1, n); return .plane(o) }
            if case .scalar(var s) = b, case .plane(let p) = a { let o = p.copy(); vDSP_vsdiv(o.pointer, 1, &s, o.pointer, 1, n); return .plane(o) }
            if case .plane(let p) = a, case .plane(let q) = b { let o = PlanarImage(width: p.width, height: p.height); vDSP_vdiv(q.pointer, 1, p.pointer, 1, o.pointer, 1, n); return .plane(o) }
        case "^":
            if case .scalar(let s) = b, case .plane(let p) = a {
                let o = p.copy(); var e = s; var cnt = Int32(o.count)
                // Guard against negative bases: clamp to 0 like PixInsight does for fractional powers.
                var lo: Float = 0, hi = Float.greatestFiniteMagnitude
                vDSP_vclip(o.pointer, 1, &lo, &hi, o.pointer, 1, n)
                vvpowsf(o.pointer, &e, o.pointer, &cnt); return .plane(o)
            }
            if case .scalar(let s) = a, case .plane(let p) = b {
                let o = p.copy(); var cnt = Int32(o.count)
                var l = log(max(s, 1e-30)); vDSP_vsmul(o.pointer, 1, &l, o.pointer, 1, n); vvexpf(o.pointer, o.pointer, &cnt); return .plane(o)
            }
            if case .plane(let p) = a, case .plane(let q) = b {
                let o = PlanarImage(width: p.width, height: p.height); var cnt = Int32(o.count)
                let base = p.copy(); var lo: Float = 0, hi = Float.greatestFiniteMagnitude
                vDSP_vclip(base.pointer, 1, &lo, &hi, base.pointer, 1, n)
                vvpowf(o.pointer, q.pointer, base.pointer, &cnt); return .plane(o)
            }
        default: break
        }
        // Generic element-wise fallback (comparisons, logic, modulo).
        let pa = materialize(a, env), pb = materialize(b, env)
        let f = try scalarOp(op)
        for i in 0..<pa.count { pa.pointer[i] = f(pa.pointer[i], pb.pointer[i]) }
        return .plane(pa)
    }

    private static func scalarOp(_ op: String) throws -> (Float, Float) -> Float {
        switch op {
        case "+": return { $0 + $1 }
        case "-": return { $0 - $1 }
        case "*": return { $0 * $1 }
        case "/": return { $0 / $1 }
        case "%": return { fmodf($0, $1) }
        case "^": return { powf($0, $1) }
        case "<": return { $0 < $1 ? 1 : 0 }
        case ">": return { $0 > $1 ? 1 : 0 }
        case "<=": return { $0 <= $1 ? 1 : 0 }
        case ">=": return { $0 >= $1 ? 1 : 0 }
        case "==": return { $0 == $1 ? 1 : 0 }
        case "!=": return { $0 != $1 ? 1 : 0 }
        case "&&": return { ($0 != 0 && $1 != 0) ? 1 : 0 }
        case "||": return { ($0 != 0 || $1 != 0) ? 1 : 0 }
        default: throw PixelMathError(message: "Unknown operator \(op)", position: 0)
        }
    }

    private static func scalarBinary(_ op: String, _ x: Float, _ y: Float) throws -> Float {
        try scalarOp(op)(x, y)
    }

    private static func unaryMath(_ v: PMValue, _ env: PixelMathEnvironment, scalar: (Float) -> Float, vforce: ((UnsafeMutablePointer<Float>, Int32) -> Void)?) -> PMValue {
        switch v {
        case .scalar(let s): return .scalar(scalar(s))
        case .plane(let p):
            let o = p.copy()
            if let vf = vforce { vf(o.pointer, Int32(o.count)) }
            else { for i in 0..<o.count { o.pointer[i] = scalar(o.pointer[i]) } }
            return .plane(o)
        }
    }

    private static func statFor(_ e: Expr, _ env: PixelMathEnvironment, _ name: String) throws -> ChannelStats {
        guard case .variable(let v) = e else { throw PixelMathError(message: "'\(name)' needs a channel name", position: 0) }
        guard let s = env.stats[v.lowercased()] else { throw PixelMathError(message: "No statistics for '\(v)'", position: 0) }
        return s
    }

    private static func call(_ name: String, _ args: [Expr], _ env: PixelMathEnvironment) throws -> PMValue {
        // Channel statistics (scalars).
        switch name {
        case "cmed": return .scalar(try statFor(args[0], env, name).median)
        case "cmean": return .scalar(try statFor(args[0], env, name).mean)
        case "cmin": return .scalar(try statFor(args[0], env, name).min)
        case "cmax": return .scalar(try statFor(args[0], env, name).max)
        case "cmad": return .scalar(try statFor(args[0], env, name).madn)
        case "cbg": return .scalar(try statFor(args[0], env, name).background)
        case "cnoise": return .scalar(try statFor(args[0], env, name).noise)
        case "cpct":
            let st = try statFor(args[0], env, name)
            guard case .scalar(let q) = try evaluate(args[1], env: env) else { throw PixelMathError(message: "cpct needs a scalar percentile", position: 0) }
            return .scalar(st.percentile(Double(q)))
        default: break
        }
        let vals = try args.map { try evaluate($0, env: env) }
        func vf(_ f: @escaping (UnsafeMutablePointer<Float>, UnsafePointer<Float>, UnsafePointer<Int32>) -> Void) -> (UnsafeMutablePointer<Float>, Int32) -> Void {
            { p, n in var c = n; f(p, p, &c) }
        }
        switch name {
        case "abs": return unaryMath(vals[0], env, scalar: { abs($0) }, vforce: { p, n in vDSP_vabs(p, 1, p, 1, vDSP_Length(n)) })
        case "sqrt": return unaryMath(vals[0], env, scalar: { sqrt(max($0, 0)) }, vforce: { p, n in var lo: Float = 0, hi = Float.greatestFiniteMagnitude; vDSP_vclip(p, 1, &lo, &hi, p, 1, vDSP_Length(n)); var c = n; vvsqrtf(p, p, &c) })
        case "exp": return unaryMath(vals[0], env, scalar: { exp($0) }, vforce: vf(vvexpf))
        case "ln", "log": return unaryMath(vals[0], env, scalar: { log($0) }, vforce: vf(vvlogf))
        case "log10": return unaryMath(vals[0], env, scalar: { log10($0) }, vforce: vf(vvlog10f))
        case "log2": return unaryMath(vals[0], env, scalar: { log2($0) }, vforce: vf(vvlog2f))
        case "sin": return unaryMath(vals[0], env, scalar: { sin($0) }, vforce: vf(vvsinf))
        case "cos": return unaryMath(vals[0], env, scalar: { cos($0) }, vforce: vf(vvcosf))
        case "tan": return unaryMath(vals[0], env, scalar: { tan($0) }, vforce: vf(vvtanf))
        case "asin": return unaryMath(vals[0], env, scalar: { asin($0) }, vforce: vf(vvasinf))
        case "acos": return unaryMath(vals[0], env, scalar: { acos($0) }, vforce: vf(vvacosf))
        case "atan": return unaryMath(vals[0], env, scalar: { atan($0) }, vforce: vf(vvatanf))
        case "sinh": return unaryMath(vals[0], env, scalar: { sinh($0) }, vforce: vf(vvsinhf))
        case "cosh": return unaryMath(vals[0], env, scalar: { cosh($0) }, vforce: vf(vvcoshf))
        case "tanh": return unaryMath(vals[0], env, scalar: { tanh($0) }, vforce: vf(vvtanhf))
        case "asinh": return unaryMath(vals[0], env, scalar: { asinh($0) }, vforce: vf(vvasinhf))
        case "floor": return unaryMath(vals[0], env, scalar: { floor($0) }, vforce: vf(vvfloorf))
        case "ceil": return unaryMath(vals[0], env, scalar: { ceil($0) }, vforce: vf(vvceilf))
        case "round": return unaryMath(vals[0], env, scalar: { $0.rounded() }, vforce: vf(vvnintf))
        case "frac": return unaryMath(vals[0], env, scalar: { $0 - floor($0) }, vforce: nil)
        case "sign": return unaryMath(vals[0], env, scalar: { $0 > 0 ? 1 : ($0 < 0 ? -1 : 0) }, vforce: nil)
        case "pow": return try binary("^", vals[0], vals[1], env)
        case "min", "max":
            var acc = vals[0]
            for v in vals.dropFirst() {
                acc = try elementwise(acc, v, env, name == "min" ? { Swift.min($0, $1) } : { Swift.max($0, $1) },
                                      vector: name == "min" ? { a, b, o, n in vDSP_vmin(a, 1, b, 1, o, 1, n) } : { a, b, o, n in vDSP_vmax(a, 1, b, 1, o, 1, n) })
            }
            return acc
        case "mean", "avg":
            var acc = vals[0]
            for v in vals.dropFirst() { acc = try binary("+", acc, v, env) }
            return try binary("/", acc, .scalar(Float(vals.count)), env)
        case "med":
            if vals.count <= 2 { return try call("mean", args, env) }
            let planes = vals.map { materialize($0, env) }
            let out = PlanarImage(width: env.width, height: env.height)
            var buf = [Float](repeating: 0, count: planes.count)
            for i in 0..<out.count {
                for (k, p) in planes.enumerated() { buf[k] = p.pointer[i] }
                buf.sort()
                out.pointer[i] = buf.count % 2 == 1 ? buf[buf.count / 2] : 0.5 * (buf[buf.count / 2 - 1] + buf[buf.count / 2])
            }
            return .plane(out)
        case "clamp":
            let lo = vals.count == 3 ? vals[1] : .scalar(0), hi = vals.count == 3 ? vals[2] : .scalar(1)
            if case .scalar(var l) = lo, case .scalar(var h) = hi {
                return map(vals[0], env) { Swift.min(Swift.max($0, l), h) } vector: { p, n in vDSP_vclip(p, 1, &l, &h, p, 1, n) }
            }
            let a = try elementwise(vals[0], lo, env, { Swift.max($0, $1) }, vector: { a, b, o, n in vDSP_vmax(a, 1, b, 1, o, 1, n) })
            return try elementwise(a, hi, env, { Swift.min($0, $1) }, vector: { a, b, o, n in vDSP_vmin(a, 1, b, 1, o, 1, n) })
        case "iif":
            if case .scalar(let c) = vals[0] { return c != 0 ? vals[1] : vals[2] }
            return select(vals[0], vals[1], vals[2], env)
        case "lerp", "mix":
            // a + (b - a) * t
            let d = try binary("-", vals[1], vals[0], env)
            let dt = try binary("*", d, vals[2], env)
            return try binary("+", vals[0], dt, env)
        case "mtf":
            guard case .scalar(let m) = vals[0] else { throw PixelMathError(message: "mtf: midtones must be a scalar", position: 0) }
            switch vals[1] {
            case .scalar(let x): return .scalar(Stretch.mtf(m, x))
            case .plane(let p):
                let o = p.copy(); o.clamp(min: 0, max: 1); Stretch.applyMTF(m, to: o); return .plane(o)
            }
        case "step":
            return try elementwise(vals[1], vals[0], env, { $0 >= $1 ? 1 : 0 }, vector: nil)
        case "smoothstep":
            let e0 = materialize(vals[0], env), e1 = materialize(vals[1], env), x = materialize(vals[2], env)
            for i in 0..<x.count {
                let t = Swift.min(Swift.max((x.pointer[i] - e0.pointer[i]) / Swift.max(e1.pointer[i] - e0.pointer[i], 1e-12), 0), 1)
                x.pointer[i] = t * t * (3 - 2 * t)
            }
            return .plane(x)
        default:
            throw PixelMathError(message: "Unknown function '\(name)'", position: 0)
        }
    }

    private static func elementwise(_ a: PMValue, _ b: PMValue, _ env: PixelMathEnvironment, _ f: (Float, Float) -> Float,
                                    vector: ((UnsafePointer<Float>, UnsafePointer<Float>, UnsafeMutablePointer<Float>, vDSP_Length) -> Void)?) throws -> PMValue {
        if case .scalar(let x) = a, case .scalar(let y) = b { return .scalar(f(x, y)) }
        let pa = materialize(a, env), pb = materialize(b, env)
        if let v = vector {
            let o = PlanarImage(width: pa.width, height: pa.height)
            v(pa.pointer, pb.pointer, o.pointer, vDSP_Length(o.count))
            return .plane(o)
        }
        for i in 0..<pa.count { pa.pointer[i] = f(pa.pointer[i], pb.pointer[i]) }
        return .plane(pa)
    }
}

// MARK: - Programs (one expression per output channel)

public struct PixelMathProgram: Codable, Equatable, Hashable {
    public var red: String
    public var green: String
    public var blue: String
    /// Optional luminance expression (empty = none).
    public var luminance: String

    public init(red: String, green: String, blue: String, luminance: String = "") {
        self.red = red; self.green = green; self.blue = blue; self.luminance = luminance
    }

    public var hasLuminance: Bool { !luminance.trimmingCharacters(in: .whitespaces).isEmpty }

    public struct Compiled {
        public let red: Expr, green: Expr, blue: Expr, luminance: Expr?
        public var requiredChannels: Set<ChannelKind> {
            var vars = red.variables.union(green.variables).union(blue.variables)
            if let l = luminance { vars.formUnion(l.variables) }
            return Set(vars.compactMap { ChannelKind.fromPixelMathIdentifier($0) })
        }
        public var unknownIdentifiers: [String] {
            var vars = red.variables.union(green.variables).union(blue.variables)
            if let l = luminance { vars.formUnion(l.variables) }
            return vars.filter { ChannelKind.fromPixelMathIdentifier($0) == nil }.sorted()
        }
    }

    public enum Slot: String { case red, green, blue, luminance }

    /// Parses all expressions; errors carry the slot name.
    public func compile() throws -> Compiled {
        func parse(_ s: String, _ slot: Slot) throws -> Expr {
            do { return try PixelMath.parse(s) } catch let e as PixelMathError {
                throw PixelMathError(message: "\(slot.rawValue.capitalized): \(e.message)", position: e.position)
            }
        }
        let r = try parse(red, .red), g = try parse(green, .green), b = try parse(blue, .blue)
        let l = hasLuminance ? try parse(luminance, .luminance) : nil
        return Compiled(red: r, green: g, blue: b, luminance: l)
    }
}
