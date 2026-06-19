namespace Quiver.Core;

/// <summary>
/// Dot product operator. When <c>regions</c> are specified, returns the sum of
/// per-region dot products (dot product is additive over disjoint spans).
/// The full-span path delegates to SIMD-vectorized <see cref="VectorScorer"/>.
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
/// Cosine similarity operator. When <c>regions</c> are specified, dot product and norms
/// are accumulated across all regions before computing the final similarity — this gives
/// the cosine of the vectors projected onto the union of the specified dimensions.
/// The full-span path delegates to SIMD-vectorized <see cref="VectorScorer"/>.
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
/// Euclidean distance operator. When <c>regions</c> are specified, squared differences
/// are accumulated across all regions before taking a single square root.
/// The full-span path delegates to SIMD-vectorized <see cref="VectorScorer"/>.
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
