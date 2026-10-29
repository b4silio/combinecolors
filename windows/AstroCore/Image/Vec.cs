using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AstroCore;

/// <summary>
/// Vectorised float kernels (the role Accelerate's vDSP plays in the macOS build). Every routine
/// works on spans of equal length; <c>dst</c> may alias a source.
/// </summary>
public static class Vec
{
    static readonly int W = Vector<float>.Count;

    public static void Fill(Span<float> dst, float v) => dst.Fill(v);

    public static void Copy(ReadOnlySpan<float> src, Span<float> dst) => src.CopyTo(dst);

    /// <summary>dst = a + b</summary>
    public static void Add(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        int n = dst.Length, i = 0;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = va[k] + vb[k];
        for (i = vd.Length * W; i < n; i++) dst[i] = a[i] + b[i];
    }

    /// <summary>dst = a - b</summary>
    public static void Sub(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = va[k] - vb[k];
        for (int i = vd.Length * W; i < n; i++) dst[i] = a[i] - b[i];
    }

    /// <summary>dst = a * b</summary>
    public static void Mul(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = va[k] * vb[k];
        for (int i = vd.Length * W; i < n; i++) dst[i] = a[i] * b[i];
    }

    /// <summary>dst = a / b</summary>
    public static void Div(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = va[k] / vb[k];
        for (int i = vd.Length * W; i < n; i++) dst[i] = a[i] / b[i];
    }

    /// <summary>dst = a * scale + offset</summary>
    public static void ScaleAdd(ReadOnlySpan<float> a, float scale, float offset, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        var vs = new Vector<float>(scale); var vo = new Vector<float>(offset);
        for (int k = 0; k < vd.Length; k++) vd[k] = va[k] * vs + vo;
        for (int i = vd.Length * W; i < n; i++) dst[i] = a[i] * scale + offset;
    }

    /// <summary>dst = a * scale</summary>
    public static void Scale(ReadOnlySpan<float> a, float scale, Span<float> dst) => ScaleAdd(a, scale, 0, dst);

    /// <summary>dst = dst + a * scale (vDSP_vsma)</summary>
    public static void MulAdd(ReadOnlySpan<float> a, float scale, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        var vs = new Vector<float>(scale);
        for (int k = 0; k < vd.Length; k++) vd[k] += va[k] * vs;
        for (int i = vd.Length * W; i < n; i++) dst[i] += a[i] * scale;
    }

    /// <summary>dst = a * sa + b * sb (vDSP_vsmsma)</summary>
    public static void Mix(ReadOnlySpan<float> a, float sa, ReadOnlySpan<float> b, float sb, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        var vsa = new Vector<float>(sa); var vsb = new Vector<float>(sb);
        for (int k = 0; k < vd.Length; k++) vd[k] = va[k] * vsa + vb[k] * vsb;
        for (int i = vd.Length * W; i < n; i++) dst[i] = a[i] * sa + b[i] * sb;
    }

    /// <summary>dst = a * b + c (vDSP_vma)</summary>
    public static void MulAdd(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> c, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var vc = MemoryMarshal.Cast<float, Vector<float>>(c);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = va[k] * vb[k] + vc[k];
        for (int i = vd.Length * W; i < n; i++) dst[i] = a[i] * b[i] + c[i];
    }

    public static void Clip(ReadOnlySpan<float> a, float lo, float hi, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        var vl = new Vector<float>(lo); var vh = new Vector<float>(hi);
        for (int k = 0; k < vd.Length; k++) vd[k] = Vector.Min(Vector.Max(va[k], vl), vh);
        for (int i = vd.Length * W; i < n; i++) dst[i] = MathF.Min(MathF.Max(a[i], lo), hi);
    }

    public static void Min(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = Vector.Min(va[k], vb[k]);
        for (int i = vd.Length * W; i < n; i++) dst[i] = MathF.Min(a[i], b[i]);
    }

    public static void Max(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = Vector.Max(va[k], vb[k]);
        for (int i = vd.Length * W; i < n; i++) dst[i] = MathF.Max(a[i], b[i]);
    }

    public static void Abs(ReadOnlySpan<float> a, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = Vector.Abs(va[k]);
        for (int i = vd.Length * W; i < n; i++) dst[i] = MathF.Abs(a[i]);
    }

    public static void Square(ReadOnlySpan<float> a, Span<float> dst) => Mul(a, a, dst);

    public static void Sqrt(ReadOnlySpan<float> a, Span<float> dst)
    {
        int n = dst.Length;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vd = MemoryMarshal.Cast<float, Vector<float>>(dst);
        for (int k = 0; k < vd.Length; k++) vd[k] = Vector.SquareRoot(va[k]);
        for (int i = vd.Length * W; i < n; i++) dst[i] = MathF.Sqrt(a[i]);
    }

    public static float Sum(ReadOnlySpan<float> a)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var acc = Vector<float>.Zero;
        for (int k = 0; k < va.Length; k++) acc += va[k];
        double s = Vector.Sum(acc);
        for (int i = va.Length * W; i < a.Length; i++) s += a[i];
        return (float)s;
    }

    public static float Mean(ReadOnlySpan<float> a) => a.Length == 0 ? 0 : Sum(a) / a.Length;

    public static (float min, float max) MinMax(ReadOnlySpan<float> a)
    {
        if (a.Length == 0) return (0, 0);
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        float lo, hi;
        int start;
        if (va.Length > 0)
        {
            var vlo = va[0]; var vhi = va[0];
            for (int k = 1; k < va.Length; k++) { vlo = Vector.Min(vlo, va[k]); vhi = Vector.Max(vhi, va[k]); }
            lo = vlo[0]; hi = vhi[0];
            for (int j = 1; j < W; j++) { lo = MathF.Min(lo, vlo[j]); hi = MathF.Max(hi, vhi[j]); }
            start = va.Length * W;
        }
        else { lo = a[0]; hi = a[0]; start = 1; }
        for (int i = start; i < a.Length; i++) { float v = a[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
        return (lo, hi);
    }

    /// <summary>dst = a ^ e (element-wise, scalar exponent).</summary>
    public static void Pow(ReadOnlySpan<float> a, float e, Span<float> dst)
    {
        int n = dst.Length;
        if (e == 2) { Mul(a, a, dst); return; }
        if (e == 0.5f) { Sqrt(a, dst); return; }
        for (int i = 0; i < n; i++) dst[i] = MathF.Pow(a[i], e);
    }

    /// <summary>dst = a ^ b (element-wise).</summary>
    public static void Pow(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        for (int i = 0; i < dst.Length; i++) dst[i] = MathF.Pow(a[i], b[i]);
    }

    public static void Exp(ReadOnlySpan<float> a, Span<float> dst) { for (int i = 0; i < dst.Length; i++) dst[i] = MathF.Exp(a[i]); }
    public static void Log(ReadOnlySpan<float> a, Span<float> dst) { for (int i = 0; i < dst.Length; i++) dst[i] = MathF.Log(a[i]); }
    public static void Log1p(ReadOnlySpan<float> a, Span<float> dst) { for (int i = 0; i < dst.Length; i++) dst[i] = Log1pScalar(a[i]); }
    public static void Asinh(ReadOnlySpan<float> a, Span<float> dst) { for (int i = 0; i < dst.Length; i++) dst[i] = MathF.Asinh(a[i]); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Log1pScalar(float x)
    {
        // log(1 + x) with care for small x (MathF has no log1p).
        double d = x;
        if (Math.Abs(d) < 1e-4) return (float)(d - d * d / 2 + d * d * d / 3);
        return (float)Math.Log(1 + d);
    }

    /// <summary>Linear-interpolated table lookup: dst = table[(a * scale + offset)] with clamping (vDSP_vtabi).</summary>
    public static void TableLookup(ReadOnlySpan<float> a, float scale, float offset, ReadOnlySpan<float> table, Span<float> dst)
    {
        int last = table.Length - 1;
        for (int i = 0; i < dst.Length; i++)
        {
            float pos = a[i] * scale + offset;
            if (!(pos > 0)) { dst[i] = table[0]; continue; }
            if (pos >= last) { dst[i] = table[last]; continue; }
            int k = (int)pos;
            float f = pos - k;
            dst[i] = table[k] * (1 - f) + table[k + 1] * f;
        }
    }

    /// <summary>Replaces NaN / ±Inf with 0 in place.</summary>
    public static void ZeroNonFinite(Span<float> a)
    {
        for (int i = 0; i < a.Length; i++) if (!float.IsFinite(a[i])) a[i] = 0;
    }
}

/// <summary>Parallel helpers with a work-size threshold so small images stay single-threaded.</summary>
public static class Par
{
    /// <summary>Runs <paramref name="body"/>(y0, y1) over row ranges in parallel when the work is large enough.</summary>
    public static void Rows(int height, long workPerRow, Action<int, int> body, long threshold = 200_000)
    {
        if (height <= 1 || height * workPerRow < threshold || Environment.ProcessorCount <= 1) { body(0, height); return; }
        int chunks = Math.Min(height, Environment.ProcessorCount * 4);
        int per = (height + chunks - 1) / chunks;
        Parallel.For(0, chunks, c =>
        {
            int y0 = c * per, y1 = Math.Min(height, y0 + per);
            if (y0 < y1) body(y0, y1);
        });
    }

    /// <summary>Runs <paramref name="body"/>(i) for i in [0, count) in parallel.</summary>
    public static void For(int count, Action<int> body)
    {
        if (count <= 1 || Environment.ProcessorCount <= 1) { for (int i = 0; i < count; i++) body(i); return; }
        Parallel.For(0, count, body);
    }
}

/// <summary>Separable 1-D convolutions, box filters and dilation on planes (the vImage replacements).</summary>
public static class Convolve
{
    /// <summary>
    /// Separable convolution with the same kernel along rows and columns. Edges are extended
    /// (<paramref name="edgeExtend"/>) or treated as zero.
    /// </summary>
    public static PlanarImage Separable(PlanarImage src, ReadOnlySpan<float> kernel, bool edgeExtend)
        => Separable(src, kernel, kernel, edgeExtend);

    public static PlanarImage Separable(PlanarImage src, ReadOnlySpan<float> kernelX, ReadOnlySpan<float> kernelY, bool edgeExtend)
    {
        var tmp = new PlanarImage(src.Width, src.Height);
        var kx = kernelX.ToArray(); var ky = kernelY.ToArray();
        Horizontal(src, kx, tmp, edgeExtend);
        var dst = new PlanarImage(src.Width, src.Height);
        Vertical(tmp, ky, dst, edgeExtend);
        return dst;
    }

    public static void Horizontal(PlanarImage src, float[] k, PlanarImage dst, bool edgeExtend)
    {
        int w = src.Width, h = src.Height, r = k.Length / 2;
        var s = src.Data; var d = dst.Data;
        Par.Rows(h, (long)w * k.Length, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                int row = y * w;
                // Interior (no clamping).
                int xStart = Math.Min(r, w), xEnd = Math.Max(xStart, w - r);
                for (int x = xStart; x < xEnd; x++)
                {
                    float acc = 0;
                    int b = row + x - r;
                    for (int j = 0; j < k.Length; j++) acc += k[j] * s[b + j];
                    d[row + x] = acc;
                }
                for (int x = 0; x < w; x++)
                {
                    if (x >= xStart && x < xEnd) { x = xEnd - 1; continue; }
                    float acc = 0;
                    for (int j = 0; j < k.Length; j++)
                    {
                        int xx = x + j - r;
                        if (xx < 0) { if (!edgeExtend) continue; xx = 0; }
                        else if (xx >= w) { if (!edgeExtend) continue; xx = w - 1; }
                        acc += k[j] * s[row + xx];
                    }
                    d[row + x] = acc;
                }
            }
        });
    }

    public static void Vertical(PlanarImage src, float[] k, PlanarImage dst, bool edgeExtend)
    {
        int w = src.Width, h = src.Height, r = k.Length / 2;
        var s = src.Data; var d = dst.Data;
        Par.Rows(h, (long)w * k.Length, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                var drow = d.AsSpan(y * w, w);
                drow.Clear();
                for (int j = 0; j < k.Length; j++)
                {
                    int yy = y + j - r;
                    if (yy < 0) { if (!edgeExtend) continue; yy = 0; }
                    else if (yy >= h) { if (!edgeExtend) continue; yy = h - 1; }
                    Vec.MulAdd(s.AsSpan(yy * w, w), k[j], drow);
                }
            }
        });
    }

    /// <summary>Box sum over a (2r+1)² window, edges extended.</summary>
    public static PlanarImage BoxSum(PlanarImage p, int radius)
    {
        int len = 2 * radius + 1;
        var k = new float[len];
        Array.Fill(k, 1f);
        return Separable(p, k, true);
    }

    /// <summary>Grey-level dilation: maximum over a k×k window, edges extended (vImageMax).</summary>
    public static PlanarImage DilateMax(PlanarImage src, int k)
    {
        int w = src.Width, h = src.Height, r = k / 2;
        var tmp = new PlanarImage(w, h);
        var s = src.Data; var t = tmp.Data;
        Par.Rows(h, (long)w * k, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float m = float.NegativeInfinity;
                    int a = Math.Max(0, x - r), b = Math.Min(w - 1, x + r);
                    for (int xx = a; xx <= b; xx++) m = MathF.Max(m, s[row + xx]);
                    t[row + x] = m;
                }
            }
        });
        var dst = new PlanarImage(w, h);
        var d = dst.Data;
        Par.Rows(h, (long)w * k, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                var drow = d.AsSpan(y * w, w);
                int a = Math.Max(0, y - r), b = Math.Min(h - 1, y + r);
                t.AsSpan(a * w, w).CopyTo(drow);
                for (int yy = a + 1; yy <= b; yy++) Vec.Max(drow, t.AsSpan(yy * w, w), drow);
            }
        });
        return dst;
    }
}

/// <summary>Resampling of planes to arbitrary sizes.</summary>
public static class Resample
{
    /// <summary>
    /// Resizes with bilinear interpolation on pixel centres (area-averaged when shrinking by more
    /// than 2× along an axis so that no samples are skipped).
    /// </summary>
    public static PlanarImage Resize(PlanarImage src, int nw, int nh)
    {
        if (nw == src.Width && nh == src.Height) return src.Copy();
        var cur = src;
        // Reduce by box halving while the target is less than half the size.
        while (cur.Width >= 2 * nw && cur.Height >= 2 * nh && cur.Width > 1 && cur.Height > 1) cur = cur.Downsampled2x();
        return Bilinear(cur, nw, nh);
    }

    public static PlanarImage Bilinear(PlanarImage src, int nw, int nh)
    {
        int sw = src.Width, sh = src.Height;
        var dst = new PlanarImage(nw, nh);
        var s = src.Data; var d = dst.Data;
        double sx = (double)sw / nw, sy = (double)sh / nh;
        var x0s = new int[nw]; var x1s = new int[nw]; var fxs = new float[nw];
        for (int x = 0; x < nw; x++)
        {
            double cx = (x + 0.5) * sx - 0.5;
            if (cx < 0) cx = 0; if (cx > sw - 1) cx = sw - 1;
            int ix = (int)cx; x0s[x] = ix; x1s[x] = Math.Min(ix + 1, sw - 1); fxs[x] = (float)(cx - ix);
        }
        Par.Rows(nh, (long)nw * 4, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                double cy = (y + 0.5) * sy - 0.5;
                if (cy < 0) cy = 0; if (cy > sh - 1) cy = sh - 1;
                int iy = (int)cy, iy1 = Math.Min(iy + 1, sh - 1);
                float fy = (float)(cy - iy);
                int r0 = iy * sw, r1 = iy1 * sw, o = y * nw;
                for (int x = 0; x < nw; x++)
                {
                    float fx = fxs[x];
                    float a = s[r0 + x0s[x]] * (1 - fx) + s[r0 + x1s[x]] * fx;
                    float b = s[r1 + x0s[x]] * (1 - fx) + s[r1 + x1s[x]] * fx;
                    d[o + x] = a * (1 - fy) + b * fy;
                }
            }
        });
        return dst;
    }
}
