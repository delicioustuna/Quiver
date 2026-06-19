using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class DyadicOperatorTests
{
    private const float RelTol = 1e-5f;

    private static (float[] a, float[] b) MakePair(int dim, int seed)
    {
        var rng = new Random(seed);
        var a = new float[dim];
        var b = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            a[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            b[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }
        return (a, b);
    }

    private static void AssertClose(float actual, float expected)
    {
        float diff = MathF.Abs(actual - expected);
        float denom = MathF.Max(MathF.Abs(expected), 1f);
        (diff / denom).Should().BeLessThan(RelTol,
            $"actual={actual} expected={expected} diff={diff}");
    }

    // --- Full-span (no regions): must match VectorScorer exactly ---

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(128)]
    [InlineData(768)]
    public void Dot_full_span_matches_VectorScorer(int dim)
    {
        var (a, b) = MakePair(dim, dim);
        var op = new DotProductOp();
        AssertClose(op.Invoke(a, b), VectorScorer.Dot(a, b));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(128)]
    [InlineData(768)]
    public void Cosine_full_span_matches_VectorScorer(int dim)
    {
        var (a, b) = MakePair(dim, dim * 3);
        var op = new CosineSimilarityOp();
        AssertClose(op.Invoke(a, b), VectorScorer.Cosine(a, b));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(128)]
    [InlineData(768)]
    public void Euclidean_full_span_matches_VectorScorer(int dim)
    {
        var (a, b) = MakePair(dim, dim * 7);
        var op = new EuclideanDistanceOp();
        AssertClose(op.Invoke(a, b), VectorScorer.Euclidean(a, b));
    }

    // --- Default interface method (no regions arg) delegates correctly ---

    [Fact]
    public void Default_Invoke_without_regions_matches_full_span()
    {
        var (a, b) = MakePair(128, 42);
        IDyadicOperator<float> dot = new DotProductOp();
        IDyadicOperator<float> cos = new CosineSimilarityOp();
        IDyadicOperator<float> euc = new EuclideanDistanceOp();

        AssertClose(dot.Invoke(a, b), VectorScorer.Dot(a, b));
        AssertClose(cos.Invoke(a, b), VectorScorer.Cosine(a, b));
        AssertClose(euc.Invoke(a, b), VectorScorer.Euclidean(a, b));
    }

    // --- Regions: single region covering full span = no regions ---

    [Fact]
    public void Single_full_region_matches_no_regions()
    {
        var (a, b) = MakePair(256, 99);
        Range[] full = [Range.All];

        AssertClose(
            new DotProductOp().Invoke(a, b, full),
            new DotProductOp().Invoke(a, b));
        AssertClose(
            new CosineSimilarityOp().Invoke(a, b, full),
            new CosineSimilarityOp().Invoke(a, b));
        AssertClose(
            new EuclideanDistanceOp().Invoke(a, b, full),
            new EuclideanDistanceOp().Invoke(a, b));
    }

    // --- Regions: partial sub-ranges ---

    [Fact]
    public void Dot_regions_sum_of_slices()
    {
        var (a, b) = MakePair(100, 55);
        Range[] regions = [0..30, 50..80];

        float expected = VectorScorer.Dot(a.AsSpan(0, 30), b.AsSpan(0, 30))
                       + VectorScorer.Dot(a.AsSpan(50, 30), b.AsSpan(50, 30));

        AssertClose(new DotProductOp().Invoke(a, b, regions), expected);
    }

    [Fact]
    public void Cosine_regions_projects_onto_selected_dimensions()
    {
        var (a, b) = MakePair(100, 77);
        Range[] regions = [10..40, 60..90];

        float dot = 0f, na = 0f, nb = 0f;
        foreach (var range in regions)
        {
            var (off, len) = range.GetOffsetAndLength(100);
            for (int i = off; i < off + len; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
        }
        float expected = dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));

        AssertClose(new CosineSimilarityOp().Invoke(a, b, regions), expected);
    }

    [Fact]
    public void Euclidean_regions_accumulates_then_sqrt()
    {
        var (a, b) = MakePair(100, 88);
        Range[] regions = [0..25, 75..100];

        float sumSq = 0f;
        foreach (var range in regions)
        {
            var (off, len) = range.GetOffsetAndLength(100);
            for (int i = off; i < off + len; i++)
            {
                float d = a[i] - b[i];
                sumSq += d * d;
            }
        }
        float expected = MathF.Sqrt(sumSq);

        AssertClose(new EuclideanDistanceOp().Invoke(a, b, regions), expected);
    }

    // --- Edge: empty regions = full span ---

    [Fact]
    public void Empty_regions_span_means_full_span()
    {
        var (a, b) = MakePair(64, 33);
        ReadOnlySpan<Range> empty = ReadOnlySpan<Range>.Empty;

        AssertClose(
            new DotProductOp().Invoke(a, b, empty),
            VectorScorer.Dot(a, b));
    }

    // --- Edge: zero vectors ---

    [Fact]
    public void Zero_vectors_return_zero()
    {
        var a = new float[64];
        var b = new float[64];

        new DotProductOp().Invoke(a, b).Should().Be(0f);
        new CosineSimilarityOp().Invoke(a, b).Should().Be(0f);
        new EuclideanDistanceOp().Invoke(a, b).Should().Be(0f);
    }

    // --- Generic dispatch: struct constraint enables devirtualization ---

    [Fact]
    public void Generic_dispatch_produces_correct_results()
    {
        var (a, b) = MakePair(128, 111);

        AssertClose(
            Apply(new DotProductOp(), a, b),
            VectorScorer.Dot(a, b));
        AssertClose(
            Apply(new CosineSimilarityOp(), a, b),
            VectorScorer.Cosine(a, b));
        AssertClose(
            Apply(new EuclideanDistanceOp(), a, b),
            VectorScorer.Euclidean(a, b));
    }

    private static float Apply<TOp>(TOp op, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        where TOp : struct, IDyadicOperator<float>
        => op.Invoke(a, b);
}
