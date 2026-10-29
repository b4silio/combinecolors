using System.Globalization;

namespace AstroCore;

public sealed class PixelMathException : Exception
{
    /// <summary>The message without the position suffix.</summary>
    public string Detail { get; }
    /// <summary>Character offset in the source string.</summary>
    public int Position { get; }

    public PixelMathException(string message, int position) : base($"{message} (at {position})") { Detail = message; Position = position; }
}

// Lexer

enum TokenKind { Number, Identifier, Op, LParen, RParen, Comma, End }

readonly record struct Token(TokenKind Kind, string Text, float Number, int Position)
{
    public bool IsOp(string op) => Kind == TokenKind.Op && Text == op;
}

static class Lexer
{
    public static List<Token> Tokenize(string s)
    {
        var chars = s;
        var out_ = new List<Token>();
        int pos = 0;
        while (true)
        {
            while (pos < chars.Length && char.IsWhiteSpace(chars[pos])) pos++;
            if (pos >= chars.Length) { out_.Add(new Token(TokenKind.End, "", 0, pos)); return out_; }
            int start = pos;
            char c = chars[pos];
            if (char.IsDigit(c) || (c == '.' && pos + 1 < chars.Length && char.IsDigit(chars[pos + 1])))
            {
                var sb = new System.Text.StringBuilder();
                while (pos < chars.Length && (char.IsDigit(chars[pos]) || chars[pos] == '.')) { sb.Append(chars[pos]); pos++; }
                if (pos < chars.Length && (chars[pos] == 'e' || chars[pos] == 'E'))
                {
                    int look = pos + 1;
                    var exp = new System.Text.StringBuilder("e");
                    if (look < chars.Length && (chars[look] == '+' || chars[look] == '-')) { exp.Append(chars[look]); look++; }
                    if (look < chars.Length && char.IsDigit(chars[look]))
                    {
                        while (look < chars.Length && char.IsDigit(chars[look])) { exp.Append(chars[look]); look++; }
                        sb.Append(exp); pos = look;
                    }
                }
                var text = sb.ToString();
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new PixelMathException($"Invalid number '{text}'", start);
                out_.Add(new Token(TokenKind.Number, text, v, start));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var sb = new System.Text.StringBuilder();
                while (pos < chars.Length && (char.IsLetterOrDigit(chars[pos]) || chars[pos] == '_' || chars[pos] == '$')) { sb.Append(chars[pos]); pos++; }
                out_.Add(new Token(TokenKind.Identifier, sb.ToString(), 0, start));
                continue;
            }
            pos++;
            switch (c)
            {
                case '(': out_.Add(new Token(TokenKind.LParen, "(", 0, start)); break;
                case ')': out_.Add(new Token(TokenKind.RParen, ")", 0, start)); break;
                case ',': out_.Add(new Token(TokenKind.Comma, ",", 0, start)); break;
                case '+': case '-': case '*': case '/': case '%': case '^': case '~': case '?': case ':':
                    out_.Add(new Token(TokenKind.Op, c.ToString(), 0, start)); break;
                case '<': case '>': case '=': case '!':
                    if (pos < chars.Length && chars[pos] == '=') { pos++; out_.Add(new Token(TokenKind.Op, c + "=", 0, start)); break; }
                    if (c == '=') throw new PixelMathException("Use '==' for comparison", start);
                    out_.Add(new Token(TokenKind.Op, c.ToString(), 0, start)); break;
                case '&':
                    if (pos < chars.Length && chars[pos] == '&') pos++;
                    out_.Add(new Token(TokenKind.Op, "&&", 0, start)); break;
                case '|':
                    if (pos < chars.Length && chars[pos] == '|') pos++;
                    out_.Add(new Token(TokenKind.Op, "||", 0, start)); break;
                default:
                    throw new PixelMathException($"Unexpected character '{c}'", start);
            }
        }
    }
}

// AST

public abstract record Expr
{
    public sealed record Number(float Value) : Expr;
    public sealed record Variable(string Name) : Expr;
    public sealed record Unary(string Op, Expr Operand) : Expr;
    public sealed record Binary(string Op, Expr Left, Expr Right) : Expr;
    public sealed record Call(string Name, IReadOnlyList<Expr> Args) : Expr
    {
        public bool Equals(Call? o) => o != null && Name == o.Name && Args.SequenceEqual(o.Args);
        public override int GetHashCode() => HashCode.Combine(Name, Args.Count);
    }
    public sealed record Ternary(Expr Cond, Expr Then, Expr Else) : Expr;

    /// <summary>Every identifier referenced (lower-cased), excluding constants.</summary>
    public HashSet<string> Variables
    {
        get
        {
            var set = new HashSet<string>();
            Collect(set);
            return set;
        }
    }

    void Collect(HashSet<string> set)
    {
        switch (this)
        {
            case Variable v: if (!PixelMath.Constants.ContainsKey(v.Name.ToLowerInvariant())) set.Add(v.Name.ToLowerInvariant()); break;
            case Unary u: u.Operand.Collect(set); break;
            case Binary b: b.Left.Collect(set); b.Right.Collect(set); break;
            case Call c: foreach (var a in c.Args) a.Collect(set); break;
            case Ternary t: t.Cond.Collect(set); t.Then.Collect(set); t.Else.Collect(set); break;
        }
    }
}

// Parser

sealed class Parser
{
    readonly List<Token> tokens;
    int i;

    public Parser(string source) { tokens = Lexer.Tokenize(source); }

    Token Current => tokens[i];
    int Position => tokens[i].Position;

    public Expr ParseProgram()
    {
        if (Current.Kind == TokenKind.End) throw new PixelMathException("Empty expression", 0);
        var e = ParseTernary();
        if (Current.Kind != TokenKind.End) throw new PixelMathException("Unexpected token", Position);
        return e;
    }

    Expr ParseTernary()
    {
        var c = ParseOr();
        if (Current.IsOp("?"))
        {
            i++;
            var a = ParseTernary();
            if (!Current.IsOp(":")) throw new PixelMathException("Expected ':'", Position);
            i++;
            var b = ParseTernary();
            return new Expr.Ternary(c, a, b);
        }
        return c;
    }

    Expr ParseOr()
    {
        var l = ParseAnd();
        while (Current.IsOp("||")) { i++; l = new Expr.Binary("||", l, ParseAnd()); }
        return l;
    }

    Expr ParseAnd()
    {
        var l = ParseEquality();
        while (Current.IsOp("&&")) { i++; l = new Expr.Binary("&&", l, ParseEquality()); }
        return l;
    }

    Expr ParseEquality()
    {
        var l = ParseComparison();
        while (Current.Kind == TokenKind.Op && (Current.Text == "==" || Current.Text == "!=")) { var o = Current.Text; i++; l = new Expr.Binary(o, l, ParseComparison()); }
        return l;
    }

    Expr ParseComparison()
    {
        var l = ParseAdditive();
        while (Current.Kind == TokenKind.Op && Current.Text is "<" or ">" or "<=" or ">=") { var o = Current.Text; i++; l = new Expr.Binary(o, l, ParseAdditive()); }
        return l;
    }

    Expr ParseAdditive()
    {
        var l = ParseMultiplicative();
        while (Current.Kind == TokenKind.Op && (Current.Text == "+" || Current.Text == "-")) { var o = Current.Text; i++; l = new Expr.Binary(o, l, ParseMultiplicative()); }
        return l;
    }

    Expr ParseMultiplicative()
    {
        var l = ParseUnary();
        while (Current.Kind == TokenKind.Op && Current.Text is "*" or "/" or "%") { var o = Current.Text; i++; l = new Expr.Binary(o, l, ParseUnary()); }
        return l;
    }

    Expr ParseUnary()
    {
        if (Current.Kind == TokenKind.Op && Current.Text is "-" or "~" or "!")
        {
            var o = Current.Text; i++;
            return new Expr.Unary(o, ParseUnary());
        }
        if (Current.IsOp("+")) { i++; return ParseUnary(); }
        return ParsePower();
    }

    Expr ParsePower()
    {
        var b = ParsePrimary();
        if (Current.IsOp("^"))
        {
            i++;
            var e = ParseUnary();   // right associative, allows ~x / -x exponents
            return new Expr.Binary("^", b, e);
        }
        return b;
    }

    Expr ParsePrimary()
    {
        int p = Position;
        switch (Current.Kind)
        {
            case TokenKind.Number: { var v = Current.Number; i++; return new Expr.Number(v); }
            case TokenKind.Identifier:
            {
                var name = Current.Text; i++;
                if (Current.Kind == TokenKind.LParen)
                {
                    i++;
                    var args = new List<Expr>();
                    if (Current.Kind != TokenKind.RParen)
                    {
                        while (true)
                        {
                            args.Add(ParseTernary());
                            if (Current.Kind == TokenKind.Comma) { i++; continue; }
                            break;
                        }
                    }
                    if (Current.Kind != TokenKind.RParen) throw new PixelMathException("Expected ')'", Position);
                    i++;
                    var lname = name.ToLowerInvariant();
                    if (!PixelMath.FunctionArity.TryGetValue(lname, out var arity)) throw new PixelMathException($"Unknown function '{name}'", p);
                    if (arity.Fixed != null && !arity.Fixed.Contains(args.Count))
                        throw new PixelMathException($"'{name}' expects {string.Join(" or ", arity.Fixed)} argument(s)", p);
                    if (arity.Min is int minA && args.Count < minA)
                        throw new PixelMathException($"'{name}' expects at least {minA} arguments", p);
                    return new Expr.Call(lname, args);
                }
                return new Expr.Variable(name);
            }
            case TokenKind.LParen:
            {
                i++;
                var e = ParseTernary();
                if (Current.Kind != TokenKind.RParen) throw new PixelMathException("Expected ')'", Position);
                i++;
                return e;
            }
            case TokenKind.RParen: throw new PixelMathException("Unexpected ')'", p);
            case TokenKind.Comma: throw new PixelMathException("Unexpected ','", p);
            case TokenKind.Op: throw new PixelMathException($"Unexpected operator '{Current.Text}'", p);
            default: throw new PixelMathException("Unexpected end of expression", p);
        }
    }
}

// Values and evaluation environment

public readonly struct PMValue
{
    public readonly float Scalar;
    public readonly PlanarImage? Plane;
    public bool IsPlane => Plane != null;
    PMValue(float s, PlanarImage? p) { Scalar = s; Plane = p; }
    public static PMValue Of(float s) => new(s, null);
    public static PMValue Of(PlanarImage p) => new(0, p);
}

/// <summary>Variables available to an expression.</summary>
public sealed class PixelMathEnvironment
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Planes keyed by canonical lower-case identifiers (all aliases registered).</summary>
    public Dictionary<string, PlanarImage> Planes { get; } = new();
    public Dictionary<string, ChannelStats> Stats { get; } = new();

    public PixelMathEnvironment(int width, int height) { Width = width; Height = height; }

    public void Set(ChannelKind kind, PlanarImage plane, ChannelStats? stats = null)
    {
        foreach (var id in kind.PixelMathIdentifiers())
        {
            Planes[id] = plane;
            if (stats != null) Stats[id] = stats;
        }
    }

    public void Set(string name, PlanarImage plane, ChannelStats? stats = null)
    {
        Planes[name.ToLowerInvariant()] = plane;
        if (stats != null) Stats[name.ToLowerInvariant()] = stats;
    }
}

public static class PixelMath
{
    public static readonly Dictionary<string, float> Constants = new() { ["pi"] = MathF.PI, ["e"] = MathF.E };

    public readonly record struct Arity(int[]? Fixed, int? Min);

    static Arity F(params int[] fixedCounts) => new(fixedCounts, null);
    static Arity AtLeast(int n) => new(null, n);

    public static readonly Dictionary<string, Arity> FunctionArity = new()
    {
        ["abs"] = F(1), ["sqrt"] = F(1), ["exp"] = F(1),
        ["ln"] = F(1), ["log"] = F(1), ["log10"] = F(1), ["log2"] = F(1),
        ["sin"] = F(1), ["cos"] = F(1), ["tan"] = F(1),
        ["asin"] = F(1), ["acos"] = F(1), ["atan"] = F(1),
        ["sinh"] = F(1), ["cosh"] = F(1), ["tanh"] = F(1), ["asinh"] = F(1),
        ["sign"] = F(1), ["floor"] = F(1), ["ceil"] = F(1), ["round"] = F(1), ["frac"] = F(1),
        ["pow"] = F(2), ["min"] = AtLeast(1), ["max"] = AtLeast(1),
        ["mean"] = AtLeast(1), ["avg"] = AtLeast(1), ["med"] = AtLeast(1),
        ["clamp"] = F(1, 3), ["iif"] = F(3), ["lerp"] = F(3), ["mix"] = F(3),
        ["mtf"] = F(2), ["step"] = F(2), ["smoothstep"] = F(3),
        ["cmed"] = F(1), ["cmean"] = F(1), ["cmin"] = F(1), ["cmax"] = F(1),
        ["cmad"] = F(1), ["cbg"] = F(1), ["cnoise"] = F(1), ["cpct"] = F(2),
    };

    /// <summary>Human readable help, shown in the Pixel Math editor.</summary>
    public static readonly (string title, string text)[] Help =
    [
        ("Operators", "+ - * / % ^   ~x = 1 - x   -x   !x   comparisons < > <= >= == !=   && ||   c ? a : b"),
        ("Channels", "Ha (H, Halpha), OIII (O, O3), SII (S, S2), Hb, R, G, B, L — case-insensitive"),
        ("Math", "abs sqrt exp ln log10 log2 pow(x,y) sin cos tan asin acos atan sinh cosh tanh asinh sign floor ceil round frac"),
        ("Pixel ops", "min(a,b,…) max(a,b,…) mean(a,b,…) med(a,b,…) clamp(x[,lo,hi]) iif(cond,a,b) lerp(a,b,t) step(edge,x) smoothstep(e0,e1,x) mtf(m,x)"),
        ("Channel stats", "cmed(X) cmean(X) cmin(X) cmax(X) cmad(X) cbg(X) cnoise(X) cpct(X, q) with q in 0…1 — scalars from the whole channel"),
        ("Constants", "pi, e"),
    ];

    public static Expr Parse(string source) => new Parser(source).ParseProgram();

    public static bool TryParse(string source, out Expr? expr)
    {
        try { expr = Parse(source); return true; }
        catch (PixelMathException) { expr = null; return false; }
    }

    // Evaluation

    public static PlanarImage Evaluate(string source, PixelMathEnvironment env) => EvaluateToPlane(Parse(source), env);

    public static PlanarImage EvaluateToPlane(Expr e, PixelMathEnvironment env)
    {
        var v = Evaluate(e, env);
        return v.IsPlane ? v.Plane! : new PlanarImage(env.Width, env.Height, v.Scalar);
    }

    /// <summary>Scalar reference evaluation for one pixel (used by tests / read-outs).</summary>
    public static float EvaluateScalar(Expr e, IReadOnlyDictionary<string, float> variables, IReadOnlyDictionary<string, ChannelStats>? stats = null)
    {
        var env = new PixelMathEnvironment(1, 1);
        foreach (var (k, v) in variables)
        {
            var plane = new PlanarImage(1, 1, v);
            ChannelStats? st = null;
            stats?.TryGetValue(k.ToLowerInvariant(), out st);
            if (ChannelKinds.FromPixelMathIdentifier(k) is ChannelKind kind) env.Set(kind, plane, st);
            else env.Set(k, plane, st);
        }
        if (stats != null) foreach (var (k, s) in stats) env.Stats[k.ToLowerInvariant()] = s;
        var r = Evaluate(e, env);
        return r.IsPlane ? r.Plane!.Data[0] : r.Scalar;
    }

    public static PMValue Evaluate(Expr e, PixelMathEnvironment env)
    {
        switch (e)
        {
            case Expr.Number n: return PMValue.Of(n.Value);
            case Expr.Variable v:
            {
                var key = v.Name.ToLowerInvariant();
                if (Constants.TryGetValue(key, out var c)) return PMValue.Of(c);
                if (env.Planes.TryGetValue(key, out var p)) return PMValue.Of(p);
                throw new PixelMathException($"Unknown variable '{v.Name}'", 0);
            }
            case Expr.Unary u:
            {
                var v = Evaluate(u.Operand, env);
                switch (u.Op)
                {
                    case "-": return Map(v, env, x => -x, s => Vec.Scale(s, -1, s));
                    case "~": return Map(v, env, x => 1 - x, s => Vec.ScaleAdd(s, -1, 1, s));
                    case "!": return Map(v, env, x => x == 0 ? 1 : 0, s => { for (int i = 0; i < s.Length; i++) s[i] = s[i] == 0 ? 1 : 0; });
                    default: throw new PixelMathException($"Unknown unary operator {u.Op}", 0);
                }
            }
            case Expr.Binary b:
            {
                var va = Evaluate(b.Left, env); var vb = Evaluate(b.Right, env);
                return BinaryOp(b.Op, va, vb, env);
            }
            case Expr.Ternary t:
            {
                var vc = Evaluate(t.Cond, env);
                if (!vc.IsPlane) return Evaluate(vc.Scalar != 0 ? t.Then : t.Else, env);
                var va = Evaluate(t.Then, env); var vb = Evaluate(t.Else, env);
                return Select(vc, va, vb, env);
            }
            case Expr.Call c: return CallFn(c.Name, c.Args, env);
            default: throw new PixelMathException("Bad expression", 0);
        }
    }

    // Helpers

    delegate void SpanAction(Span<float> s);

    static PlanarImage Materialize(PMValue v, PixelMathEnvironment env)
        => v.IsPlane ? v.Plane!.Copy() : new PlanarImage(env.Width, env.Height, v.Scalar);

    static void ParApply(PlanarImage p, SpanAction body)
    {
        Par.Rows(p.Height, p.Width, (y0, y1) => body(p.Data.AsSpan(y0 * p.Width, (y1 - y0) * p.Width)));
    }

    static PMValue Map(PMValue v, PixelMathEnvironment env, Func<float, float> scalar, SpanAction vector)
    {
        if (!v.IsPlane) return PMValue.Of(scalar(v.Scalar));
        var out_ = v.Plane!.Copy();
        ParApply(out_, vector);
        return PMValue.Of(out_);
    }

    static PMValue Select(PMValue c, PMValue a, PMValue b, PixelMathEnvironment env)
    {
        var cp = Materialize(c, env); var ap = Materialize(a, env); var bp = Materialize(b, env);
        var cd = cp.Data; var ad = ap.Data; var bd = bp.Data;
        Par.Rows(cp.Height, cp.Width, (y0, y1) => { for (int i = y0 * cp.Width; i < y1 * cp.Width; i++) cd[i] = cd[i] != 0 ? ad[i] : bd[i]; });
        return PMValue.Of(cp);
    }

    static PMValue BinaryOp(string op, PMValue a, PMValue b, PixelMathEnvironment env)
    {
        if (!a.IsPlane && !b.IsPlane) return PMValue.Of(ScalarOp(op)(a.Scalar, b.Scalar));
        switch (op)
        {
            case "+":
                if (!a.IsPlane) { var o = b.Plane!.Copy(); Vec.ScaleAdd(o.Data, 1, a.Scalar, o.Data); return PMValue.Of(o); }
                if (!b.IsPlane) { var o = a.Plane!.Copy(); Vec.ScaleAdd(o.Data, 1, b.Scalar, o.Data); return PMValue.Of(o); }
                { var o = PlanarImage.Uninitialized(a.Plane!.Width, a.Plane.Height); Vec.Add(a.Plane.Data, b.Plane!.Data, o.Data); return PMValue.Of(o); }
            case "-":
                if (!a.IsPlane) { var o = b.Plane!.Copy(); Vec.ScaleAdd(o.Data, -1, a.Scalar, o.Data); return PMValue.Of(o); }
                if (!b.IsPlane) { var o = a.Plane!.Copy(); Vec.ScaleAdd(o.Data, 1, -b.Scalar, o.Data); return PMValue.Of(o); }
                { var o = PlanarImage.Uninitialized(a.Plane!.Width, a.Plane.Height); Vec.Sub(a.Plane.Data, b.Plane!.Data, o.Data); return PMValue.Of(o); }
            case "*":
                if (!a.IsPlane) { var o = b.Plane!.Copy(); Vec.Scale(o.Data, a.Scalar, o.Data); return PMValue.Of(o); }
                if (!b.IsPlane) { var o = a.Plane!.Copy(); Vec.Scale(o.Data, b.Scalar, o.Data); return PMValue.Of(o); }
                { var o = PlanarImage.Uninitialized(a.Plane!.Width, a.Plane.Height); Vec.Mul(a.Plane.Data, b.Plane!.Data, o.Data); return PMValue.Of(o); }
            case "/":
                if (!a.IsPlane) { var o = b.Plane!.Copy(); float s = a.Scalar; ParApply(o, sp => { for (int i = 0; i < sp.Length; i++) sp[i] = s / sp[i]; }); return PMValue.Of(o); }
                if (!b.IsPlane) { var o = a.Plane!.Copy(); Vec.Scale(o.Data, 1 / b.Scalar, o.Data); return PMValue.Of(o); }
                { var o = PlanarImage.Uninitialized(a.Plane!.Width, a.Plane.Height); Vec.Div(a.Plane.Data, b.Plane!.Data, o.Data); return PMValue.Of(o); }
            case "^":
                if (!b.IsPlane)
                {
                    // Guard against negative bases: clamp to 0 like PixInsight does for fractional powers.
                    var o = a.Plane!.Copy(); float e = b.Scalar;
                    Vec.Clip(o.Data, 0, float.MaxValue, o.Data);
                    ParApply(o, sp => Vec.Pow(sp, e, sp));
                    return PMValue.Of(o);
                }
                if (!a.IsPlane)
                {
                    var o = b.Plane!.Copy(); float l = MathF.Log(MathF.Max(a.Scalar, 1e-30f));
                    ParApply(o, sp => { for (int i = 0; i < sp.Length; i++) sp[i] = MathF.Exp(l * sp[i]); });
                    return PMValue.Of(o);
                }
                {
                    var o = PlanarImage.Uninitialized(a.Plane!.Width, a.Plane.Height);
                    var ad = a.Plane.Data; var bd = b.Plane!.Data; var od = o.Data;
                    Par.Rows(o.Height, o.Width, (y0, y1) => { for (int i = y0 * o.Width; i < y1 * o.Width; i++) od[i] = MathF.Pow(MathF.Max(ad[i], 0), bd[i]); });
                    return PMValue.Of(o);
                }
        }
        // Generic element-wise fallback (comparisons, logic, modulo).
        var pa = Materialize(a, env); var pb = Materialize(b, env);
        var f = ScalarOp(op);
        var pad = pa.Data; var pbd = pb.Data;
        Par.Rows(pa.Height, pa.Width, (y0, y1) => { for (int i = y0 * pa.Width; i < y1 * pa.Width; i++) pad[i] = f(pad[i], pbd[i]); });
        return PMValue.Of(pa);
    }

    static Func<float, float, float> ScalarOp(string op) => op switch
    {
        "+" => (x, y) => x + y,
        "-" => (x, y) => x - y,
        "*" => (x, y) => x * y,
        "/" => (x, y) => x / y,
        "%" => (x, y) => x % y,
        "^" => (x, y) => MathF.Pow(x, y),
        "<" => (x, y) => x < y ? 1 : 0,
        ">" => (x, y) => x > y ? 1 : 0,
        "<=" => (x, y) => x <= y ? 1 : 0,
        ">=" => (x, y) => x >= y ? 1 : 0,
        "==" => (x, y) => x == y ? 1 : 0,
        "!=" => (x, y) => x != y ? 1 : 0,
        "&&" => (x, y) => (x != 0 && y != 0) ? 1 : 0,
        "||" => (x, y) => (x != 0 || y != 0) ? 1 : 0,
        _ => throw new PixelMathException($"Unknown operator {op}", 0),
    };

    static PMValue UnaryMath(PMValue v, Func<float, float> f)
    {
        if (!v.IsPlane) return PMValue.Of(f(v.Scalar));
        var o = v.Plane!.Copy();
        ParApply(o, sp => { for (int i = 0; i < sp.Length; i++) sp[i] = f(sp[i]); });
        return PMValue.Of(o);
    }

    static ChannelStats StatFor(Expr e, PixelMathEnvironment env, string name)
    {
        if (e is not Expr.Variable v) throw new PixelMathException($"'{name}' needs a channel name", 0);
        if (!env.Stats.TryGetValue(v.Name.ToLowerInvariant(), out var s)) throw new PixelMathException($"No statistics for '{v.Name}'", 0);
        return s;
    }

    static PMValue CallFn(string name, IReadOnlyList<Expr> args, PixelMathEnvironment env)
    {
        // Channel statistics (scalars).
        switch (name)
        {
            case "cmed": return PMValue.Of(StatFor(args[0], env, name).Median);
            case "cmean": return PMValue.Of(StatFor(args[0], env, name).Mean);
            case "cmin": return PMValue.Of(StatFor(args[0], env, name).Min);
            case "cmax": return PMValue.Of(StatFor(args[0], env, name).Max);
            case "cmad": return PMValue.Of(StatFor(args[0], env, name).Madn);
            case "cbg": return PMValue.Of(StatFor(args[0], env, name).Background);
            case "cnoise": return PMValue.Of(StatFor(args[0], env, name).Noise);
            case "cpct":
            {
                var st = StatFor(args[0], env, name);
                var q = Evaluate(args[1], env);
                if (q.IsPlane) throw new PixelMathException("cpct needs a scalar percentile", 0);
                return PMValue.Of(st.Percentile(q.Scalar));
            }
        }
        var vals = args.Select(a => Evaluate(a, env)).ToList();
        switch (name)
        {
            case "abs": return UnaryMath(vals[0], MathF.Abs);
            case "sqrt": return UnaryMath(vals[0], x => MathF.Sqrt(MathF.Max(x, 0)));
            case "exp": return UnaryMath(vals[0], MathF.Exp);
            case "ln": case "log": return UnaryMath(vals[0], MathF.Log);
            case "log10": return UnaryMath(vals[0], MathF.Log10);
            case "log2": return UnaryMath(vals[0], MathF.Log2);
            case "sin": return UnaryMath(vals[0], MathF.Sin);
            case "cos": return UnaryMath(vals[0], MathF.Cos);
            case "tan": return UnaryMath(vals[0], MathF.Tan);
            case "asin": return UnaryMath(vals[0], MathF.Asin);
            case "acos": return UnaryMath(vals[0], MathF.Acos);
            case "atan": return UnaryMath(vals[0], MathF.Atan);
            case "sinh": return UnaryMath(vals[0], MathF.Sinh);
            case "cosh": return UnaryMath(vals[0], MathF.Cosh);
            case "tanh": return UnaryMath(vals[0], MathF.Tanh);
            case "asinh": return UnaryMath(vals[0], MathF.Asinh);
            case "floor": return UnaryMath(vals[0], MathF.Floor);
            case "ceil": return UnaryMath(vals[0], MathF.Ceiling);
            case "round": return UnaryMath(vals[0], x => MathF.Round(x, MidpointRounding.AwayFromZero));
            case "frac": return UnaryMath(vals[0], x => x - MathF.Floor(x));
            case "sign": return UnaryMath(vals[0], x => x > 0 ? 1 : (x < 0 ? -1 : 0));
            case "pow": return BinaryOp("^", vals[0], vals[1], env);
            case "min":
            case "max":
            {
                var acc = vals[0];
                bool isMin = name == "min";
                for (int k = 1; k < vals.Count; k++)
                    acc = ElementWise(acc, vals[k], env, isMin ? MathF.Min : MathF.Max, isMin ? Vec.Min : Vec.Max);
                return acc;
            }
            case "mean":
            case "avg":
            {
                var acc = vals[0];
                for (int k = 1; k < vals.Count; k++) acc = BinaryOp("+", acc, vals[k], env);
                return BinaryOp("/", acc, PMValue.Of(vals.Count), env);
            }
            case "med":
            {
                if (vals.Count <= 2) return CallFn("mean", args, env);
                var planes = vals.Select(v => Materialize(v, env)).ToArray();
                var out_ = PlanarImage.Uninitialized(env.Width, env.Height);
                var od = out_.Data;
                Par.Rows(out_.Height, out_.Width * planes.Length, (y0, y1) =>
                {
                    var buf = new float[planes.Length];
                    for (int i = y0 * out_.Width; i < y1 * out_.Width; i++)
                    {
                        for (int k = 0; k < planes.Length; k++) buf[k] = planes[k].Data[i];
                        Array.Sort(buf);
                        od[i] = buf.Length % 2 == 1 ? buf[buf.Length / 2] : 0.5f * (buf[buf.Length / 2 - 1] + buf[buf.Length / 2]);
                    }
                });
                return PMValue.Of(out_);
            }
            case "clamp":
            {
                var lo = vals.Count == 3 ? vals[1] : PMValue.Of(0); var hi = vals.Count == 3 ? vals[2] : PMValue.Of(1);
                if (!lo.IsPlane && !hi.IsPlane)
                {
                    float l = lo.Scalar, h = hi.Scalar;
                    return Map(vals[0], env, x => MathF.Min(MathF.Max(x, l), h), s => Vec.Clip(s, l, h, s));
                }
                var a = ElementWise(vals[0], lo, env, MathF.Max, Vec.Max);
                return ElementWise(a, hi, env, MathF.Min, Vec.Min);
            }
            case "iif":
                if (!vals[0].IsPlane) return vals[0].Scalar != 0 ? vals[1] : vals[2];
                return Select(vals[0], vals[1], vals[2], env);
            case "lerp":
            case "mix":
            {
                // a + (b - a) * t
                var d = BinaryOp("-", vals[1], vals[0], env);
                var dt = BinaryOp("*", d, vals[2], env);
                return BinaryOp("+", vals[0], dt, env);
            }
            case "mtf":
            {
                if (vals[0].IsPlane) throw new PixelMathException("mtf: midtones must be a scalar", 0);
                float m = vals[0].Scalar;
                if (!vals[1].IsPlane) return PMValue.Of(Stretch.Mtf(m, vals[1].Scalar));
                var o = vals[1].Plane!.Copy(); o.Clamp(0, 1); Stretch.ApplyMtf(m, o); return PMValue.Of(o);
            }
            case "step":
                return ElementWise(vals[1], vals[0], env, (x, edge) => x >= edge ? 1 : 0, null);
            case "smoothstep":
            {
                var e0 = Materialize(vals[0], env); var e1 = Materialize(vals[1], env); var x = Materialize(vals[2], env);
                var e0d = e0.Data; var e1d = e1.Data; var xd = x.Data;
                Par.Rows(x.Height, x.Width, (y0, y1) =>
                {
                    for (int i = y0 * x.Width; i < y1 * x.Width; i++)
                    {
                        float t = MathF.Min(MathF.Max((xd[i] - e0d[i]) / MathF.Max(e1d[i] - e0d[i], 1e-12f), 0), 1);
                        xd[i] = t * t * (3 - 2 * t);
                    }
                });
                return PMValue.Of(x);
            }
            default:
                throw new PixelMathException($"Unknown function '{name}'", 0);
        }
    }

    delegate void VecBinary(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst);

    static PMValue ElementWise(PMValue a, PMValue b, PixelMathEnvironment env, Func<float, float, float> f, VecBinary? vector)
    {
        if (!a.IsPlane && !b.IsPlane) return PMValue.Of(f(a.Scalar, b.Scalar));
        var pa = Materialize(a, env); var pb = Materialize(b, env);
        if (vector != null)
        {
            var o = PlanarImage.Uninitialized(pa.Width, pa.Height);
            vector(pa.Data, pb.Data, o.Data);
            return PMValue.Of(o);
        }
        var pad = pa.Data; var pbd = pb.Data;
        Par.Rows(pa.Height, pa.Width, (y0, y1) => { for (int i = y0 * pa.Width; i < y1 * pa.Width; i++) pad[i] = f(pad[i], pbd[i]); });
        return PMValue.Of(pa);
    }
}

// Programs (one expression per output channel)

public sealed partial class PixelMathProgram : IEquatable<PixelMathProgram>
{
    public string Red;
    public string Green;
    public string Blue;
    /// <summary>Optional luminance expression (empty = none).</summary>
    public string Luminance;

    public PixelMathProgram(string red, string green, string blue, string luminance = "")
    {
        Red = red; Green = green; Blue = blue; Luminance = luminance;
    }

    public PixelMathProgram Clone() => new(Red, Green, Blue, Luminance);

    public bool HasLuminance => !string.IsNullOrWhiteSpace(Luminance);

    public sealed class Compiled
    {
        public required Expr Red { get; init; }
        public required Expr Green { get; init; }
        public required Expr Blue { get; init; }
        public Expr? Luminance { get; init; }

        HashSet<string> AllVariables()
        {
            var vars = new HashSet<string>(Red.Variables);
            vars.UnionWith(Green.Variables); vars.UnionWith(Blue.Variables);
            if (Luminance != null) vars.UnionWith(Luminance.Variables);
            return vars;
        }

        public HashSet<ChannelKind> RequiredChannels
        {
            get
            {
                var set = new HashSet<ChannelKind>();
                foreach (var v in AllVariables()) if (ChannelKinds.FromPixelMathIdentifier(v) is ChannelKind k) set.Add(k);
                return set;
            }
        }

        public List<string> UnknownIdentifiers => AllVariables().Where(v => ChannelKinds.FromPixelMathIdentifier(v) == null).OrderBy(v => v, StringComparer.Ordinal).ToList();
    }

    public enum Slot { Red, Green, Blue, Luminance }

    /// <summary>Parses all expressions; errors carry the slot name.</summary>
    public Compiled Compile()
    {
        static Expr P(string s, Slot slot)
        {
            try { return PixelMath.Parse(s); }
            catch (PixelMathException e) { throw new PixelMathException($"{slot}: {e.Detail}", e.Position); }
        }
        var r = P(Red, Slot.Red); var g = P(Green, Slot.Green); var b = P(Blue, Slot.Blue);
        var l = HasLuminance ? P(Luminance, Slot.Luminance) : null;
        return new Compiled { Red = r, Green = g, Blue = b, Luminance = l };
    }

    public bool Equals(PixelMathProgram? o) => o != null && Red == o.Red && Green == o.Green && Blue == o.Blue && Luminance == o.Luminance;
    public override bool Equals(object? obj) => Equals(obj as PixelMathProgram);
    public override int GetHashCode() => HashCode.Combine(Red, Green, Blue, Luminance);
}
