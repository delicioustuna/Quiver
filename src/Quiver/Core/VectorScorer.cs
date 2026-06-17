using System.Numerics;
using System.Runtime.InteropServices;

namespace Quiver.Core;

/// <summary>
/// SIMD-vectorized distance/similarity primitives for <see cref="InMemoryVectorStore"/>.
/// Uses BCL in-box <see cref="Vector{T}"/> over <c>float</c>; no extra package
/// dependency is taken. Lane width follows <see cref="Vector{T}.Count"/>
/// (AVX2=8, AVX-512=16, scalar fallback=1) at runtime.
///
/// All methods preserve the "HIGHER = more similar" convention for the caller:
/// <see cref="Cosine"/> and <see cref="Dot"/> return similarity directly,
/// <see cref="Euclidean"/> returns the raw distance (callers should negate when
/// using it as a heap score, matching the prior <see cref="InMemoryVectorStore"/>
/// behaviour).
/// </summary>
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

/// <summary>
/// Scalar baseline kept for parity tests / benchmarks. Production code path
/// uses <see cref="VectorScorer"/> exclusively.
/// </summary>
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
