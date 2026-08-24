namespace Yatagarasu.Core;

/// <summary>
/// 内積演算子。リージョン指定時は各リージョンの内積の合計を返す (内積は互いに素なスパンに対して加法的)。
/// </summary>
public readonly struct DotProductOp : IDyadicOperator<float>
{
    /// <inheritdoc />
    public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions)
    {
        if (regions.IsEmpty)
            return VectorScorer.Dot(a, b);

        float sum = 0f;
        foreach (var range in regions)
        {
            var (offset, length) = range.GetOffsetAndLength(a.Length);
            sum += VectorScorer.Dot(a.Slice(offset, length), b.Slice(offset, length));
        }
        return sum;
    }

    /// <inheritdoc />
    public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Invoke(a, b, ReadOnlySpan<Range>.Empty);
}

/// <summary>
/// コサイン類似度演算子。リージョン指定時は全リージョンの内積とノルムを累積してから
/// 最終類似度を計算する (指定次元の和集合への射影のコサイン)。
/// </summary>
public readonly struct CosineSimilarityOp : IDyadicOperator<float>
{
    /// <inheritdoc />
    public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions)
    {
        if (regions.IsEmpty)
            return VectorScorer.Cosine(a, b);

        float dot = 0f, na = 0f, nb = 0f;
        foreach (var range in regions)
        {
            var (offset, length) = range.GetOffsetAndLength(a.Length);
            var sa = a.Slice(offset, length);
            var sb = b.Slice(offset, length);
            for (int i = 0; i < length; i++)
            {
                float x = sa[i], y = sb[i];
                dot += x * y;
                na += x * x;
                nb += y * y;
            }
        }
        float denom = MathF.Sqrt(na) * MathF.Sqrt(nb);
        return denom == 0f ? 0f : dot / denom;
    }

    /// <inheritdoc />
    public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Invoke(a, b, ReadOnlySpan<Range>.Empty);
}

/// <summary>
/// ユークリッド距離演算子。リージョン指定時は全リージョンの二乗差を累積してから平方根を取る。
/// </summary>
public readonly struct EuclideanDistanceOp : IDyadicOperator<float>
{
    /// <inheritdoc />
    public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<Range> regions)
    {
        if (regions.IsEmpty)
            return VectorScorer.Euclidean(a, b);

        float sumSq = 0f;
        foreach (var range in regions)
        {
            var (offset, length) = range.GetOffsetAndLength(a.Length);
            var sa = a.Slice(offset, length);
            var sb = b.Slice(offset, length);
            for (int i = 0; i < length; i++)
            {
                float d = sa[i] - sb[i];
                sumSq += d * d;
            }
        }
        return MathF.Sqrt(sumSq);
    }

    /// <inheritdoc />
    public float Invoke(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Invoke(a, b, ReadOnlySpan<Range>.Empty);
}
