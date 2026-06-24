using Avalonia.Media;

namespace Quiver.Studio.Models;

/// <summary>
/// 0以上1以下の <see cref="float"/> から <see cref="Avalonia.Media.Color"/> を返すカラーマップ関数群。
/// 各カラーマップは代表色テーブルを線形補間。
/// </summary>
public static class Colormaps
{
    /// <summary>制御点テーブルを t [0,1] で線形補間して HEX 文字列を返す。</summary>
    private static Color Interpolate(float[,] table, float t)
    {
        t = MathF.Max(0f, MathF.Min(1f, t));

        int n = table.GetLength(0);          // 制御点の数
        float x = t * (n - 1);
        int i = (int)MathF.Floor(x);
        if (i >= n - 1) i = n - 2;          // 末端クランプ
        float f = x - i;

        int r = (int)MathF.Round((table[i, 0] + (table[i + 1, 0] - table[i, 0]) * f) * 255);
        int g = (int)MathF.Round((table[i, 1] + (table[i + 1, 1] - table[i, 1]) * f) * 255);
        int b = (int)MathF.Round((table[i, 2] + (table[i + 1, 2] - table[i, 2]) * f) * 255);

        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);

        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }

    private static readonly float[,] _viridis = {
        { 0.267f, 0.005f, 0.329f },
        { 0.283f, 0.141f, 0.459f },
        { 0.254f, 0.265f, 0.530f },
        { 0.207f, 0.372f, 0.553f },
        { 0.164f, 0.471f, 0.558f },
        { 0.128f, 0.567f, 0.551f },
        { 0.135f, 0.659f, 0.518f },
        { 0.267f, 0.749f, 0.441f },
        { 0.478f, 0.821f, 0.318f },
        { 0.741f, 0.873f, 0.150f },
        { 0.993f, 0.906f, 0.144f },
    };

    /// <summary>Viridis カラーマップ（青紫→緑→黄）</summary>
    /// <param name="t">入力値 [0, 1]</param>
    /// <returns>"#RRGGBB" 形式の HEX 文字列</returns>
    public static Color Viridis(float t) => Interpolate(_viridis, t);

    private static readonly float[,] _turbo = {
        { 0.190f, 0.071f, 0.232f },
        { 0.254f, 0.364f, 0.854f },
        { 0.123f, 0.647f, 0.910f },
        { 0.141f, 0.843f, 0.635f },
        { 0.396f, 0.941f, 0.360f },
        { 0.667f, 0.980f, 0.196f },
        { 0.902f, 0.906f, 0.255f },
        { 0.996f, 0.714f, 0.196f },
        { 0.969f, 0.427f, 0.118f },
        { 0.863f, 0.196f, 0.067f },
        { 0.671f, 0.027f, 0.027f },
    };

    /// <summary>Turbo カラーマップ（紺→水→緑→黄→赤）</summary>
    public static Color Turbo(float t) => Interpolate(_turbo, t);

    private static readonly float[,] _inferno = {
        { 0.001f, 0.000f, 0.014f },
        { 0.133f, 0.040f, 0.191f },
        { 0.342f, 0.059f, 0.375f },
        { 0.516f, 0.080f, 0.397f },
        { 0.681f, 0.142f, 0.362f },
        { 0.824f, 0.226f, 0.280f },
        { 0.927f, 0.357f, 0.185f },
        { 0.980f, 0.510f, 0.094f },
        { 0.989f, 0.668f, 0.108f },
        { 0.970f, 0.832f, 0.376f },
        { 0.988f, 1.000f, 0.645f },
    };

    /// <summary>Inferno カラーマップ（黒→紫→橙→淡黄）</summary>
    public static Color Inferno(float t) => Interpolate(_inferno, t);

    private static readonly float[,] _plasma = {
        { 0.050f, 0.030f, 0.528f },
        { 0.232f, 0.022f, 0.602f },
        { 0.385f, 0.015f, 0.638f },
        { 0.533f, 0.017f, 0.636f },
        { 0.666f, 0.064f, 0.582f },
        { 0.775f, 0.156f, 0.499f },
        { 0.865f, 0.256f, 0.409f },
        { 0.936f, 0.366f, 0.312f },
        { 0.975f, 0.487f, 0.207f },
        { 0.990f, 0.618f, 0.100f },
        { 0.940f, 0.975f, 0.131f },
    };

    /// <summary>Plasma カラーマップ（紺紫→マゼンタ→黄）</summary>
    public static Color Plasma(float t) => Interpolate(_plasma, t);

    private static readonly float[,] _cividis = {
        { 0.000f, 0.135f, 0.304f },
        { 0.090f, 0.172f, 0.356f },
        { 0.186f, 0.209f, 0.373f },
        { 0.268f, 0.247f, 0.384f },
        { 0.344f, 0.285f, 0.393f },
        { 0.421f, 0.324f, 0.393f },
        { 0.499f, 0.365f, 0.384f },
        { 0.582f, 0.408f, 0.361f },
        { 0.668f, 0.455f, 0.321f },
        { 0.758f, 0.506f, 0.267f },
        { 0.862f, 0.572f, 0.199f },
    };

    /// <summary>Cividis カラーマップ（紺→くすんだ緑→黄茶）</summary>
    public static Color Cividis(float t) => Interpolate(_cividis, t);

    private static readonly float[,] _parula = {
        { 0.208f, 0.166f, 0.530f },
        { 0.204f, 0.294f, 0.737f },
        { 0.091f, 0.454f, 0.784f },
        { 0.000f, 0.589f, 0.741f },
        { 0.000f, 0.694f, 0.638f },
        { 0.126f, 0.765f, 0.535f },
        { 0.373f, 0.827f, 0.424f },
        { 0.619f, 0.855f, 0.301f },
        { 0.815f, 0.860f, 0.228f },
        { 0.965f, 0.860f, 0.164f },
        { 0.949f, 0.947f, 0.126f },
    };

    /// <summary>Parula カラーマップ（紺→シアン→黄）</summary>
    public static Color Parula(float t) => Interpolate(_parula, t);

    /// <summary>Gray カラーマップ（黒→白）</summary>
    public static Color Gray(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int v = (int)Math.Round(t * 255);
        return Color.FromRgb((byte)v, (byte)v, (byte)v);
    }
}