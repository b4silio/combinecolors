namespace AstroCore;

/// <summary>
/// Stand-in for <c>GpuDenoise</c> on the non-Windows build (see AstroCore.csproj): the Direct3D 11
/// compute shaders are Windows only, so the callers always take the CPU paths (non-local means, BM3D).
/// Same public surface as the real class.
/// </summary>
public static class GpuDenoise
{
    /// <summary>Set to false to force the CPU paths (tests, benchmarks). No effect here: there is no GPU path.</summary>
    public static bool Enabled = true;

    public static bool IsAvailable => false;
    public static string? DeviceName => null;
    /// <summary>Why the GPU could not be used (null when it can).</summary>
    public static string? UnavailableReason => "GPU denoise (Direct3D 11) is only available on Windows";

    internal static PlanarImage? NonLocalMeans(PlanarImage y, float sigma, float h, int search) => null;
    internal static PlanarImage? Bm3d(PlanarImage y, float sigma, float amount, int search) => null;
}
