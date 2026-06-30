using System.Numerics;
using System.Runtime.InteropServices;

namespace Quiver.Core;

// SIMD 距離 / 類似度プリミティブ。BCL の Vector<T> (float) を使用し追加依存なし。
// レーン幅はランタイムの Vector<float>.Count に従う (AVX2=8, AVX-512=16, scalar=1)。
// 規約: Cosine / Dot は類似度 (大きいほど類似) を直接返す。Euclidean は生距離を返す
// (ヒープスコアとして使う場合は呼び出し側で符号反転する)。
internal static class VectorScorer
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len = a.Length;
        int width = Vector<float>.Count;
        float s = 0f;
        int i = 0;

        if (len >= width)
        {
            var av = MemoryMarshal.Cast<float, Vector<float>>(a);
            var bv = MemoryMarshal.Cast<float, Vector<float>>(b);
            var acc = Vector<float>.Zero;
            for (int j = 0; j < av.Length; j++)
                acc += av[j] * bv[j];
            s = Vector.Dot(acc, Vector<float>.One);
            i = av.Length * width;
        }

        for (; i < len; i++)
            s += a[i] * b[i];
        return s;
    }

    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len = a.Length;
        int width = Vector<float>.Count;
        float dot = 0f, na = 0f, nb = 0f;
        int i = 0;

        if (len >= width)
        {
            var av = MemoryMarshal.Cast<float, Vector<float>>(a);
            var bv = MemoryMarshal.Cast<float, Vector<float>>(b);
            var dAcc = Vector<float>.Zero;
            var aAcc = Vector<float>.Zero;
            var bAcc = Vector<float>.Zero;
            for (int j = 0; j < av.Length; j++)
            {
                var x = av[j];
                var y = bv[j];
                dAcc += x * y;
                aAcc += x * x;
                bAcc += y * y;
            }
            dot = Vector.Dot(dAcc, Vector<float>.One);
            na = Vector.Dot(aAcc, Vector<float>.One);
            nb = Vector.Dot(bAcc, Vector<float>.One);
            i = av.Length * width;
        }

        for (; i < len; i++)
        {
            float x = a[i], y = b[i];
            dot += x * y;
            na += x * x;
            nb += y * y;
        }

        float denom = MathF.Sqrt(na) * MathF.Sqrt(nb);
        if (denom == 0f) return 0f;
        return dot / denom;
    }

    public static float Euclidean(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len = a.Length;
        int width = Vector<float>.Count;
        float s = 0f;
        int i = 0;

        if (len >= width)
        {
            var av = MemoryMarshal.Cast<float, Vector<float>>(a);
            var bv = MemoryMarshal.Cast<float, Vector<float>>(b);
            var acc = Vector<float>.Zero;
            for (int j = 0; j < av.Length; j++)
            {
                var d = av[j] - bv[j];
                acc += d * d;
            }
            s = Vector.Dot(acc, Vector<float>.One);
            i = av.Length * width;
        }

        for (; i < len; i++)
        {
            float d = a[i] - b[i];
            s += d * d;
        }
        return MathF.Sqrt(s);
    }
}

// パリティテスト / ベンチマーク用のスカラ基準実装。本番経路では VectorScorer を使う。
internal static class ScalarVectorScorer
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float s = 0f;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0f, na = 0f, nb = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        float denom = MathF.Sqrt(na) * MathF.Sqrt(nb);
        if (denom == 0f) return 0f;
        return dot / denom;
    }

    public static float Euclidean(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float s = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float d = a[i] - b[i];
            s += d * d;
        }
        return MathF.Sqrt(s);
    }
}
