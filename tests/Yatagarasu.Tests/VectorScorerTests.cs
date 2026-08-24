using FluentAssertions;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// SIMD 版 <see cref="VectorScorer"/> が <see cref="ScalarVectorScorer"/> と
/// 小さな相対許容誤差の範囲で一致することを検証する。
/// SIMD レーン未満、レーンと同数、複数レーンにまたがる各次元数を対象とする。
/// </summary>
public sealed class VectorScorerTests
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

    private static void AssertClose(float simd, float scalar)
    {
        float diff = MathF.Abs(simd - scalar);
        float denom = MathF.Max(MathF.Abs(scalar), 1f);
        (diff / denom).Should().BeLessThan(RelTol,
            $"SIMD={simd} Scalar={scalar} diff={diff}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(768)]
    [InlineData(1536)]
    public void Dot_matches_scalar(int dim)
    {
        var (a, b) = MakePair(dim, dim * 7919);
        AssertClose(VectorScorer.Dot(a, b), ScalarVectorScorer.Dot(a, b));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(768)]
    [InlineData(1536)]
    public void Cosine_matches_scalar(int dim)
    {
        var (a, b) = MakePair(dim, dim * 104729);
        AssertClose(VectorScorer.Cosine(a, b), ScalarVectorScorer.Cosine(a, b));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(768)]
    [InlineData(1536)]
    public void Euclidean_matches_scalar(int dim)
    {
        var (a, b) = MakePair(dim, dim * 15485863);
        AssertClose(VectorScorer.Euclidean(a, b), ScalarVectorScorer.Euclidean(a, b));
    }

    [Fact]
    public void All_zero_vectors_yield_zero_dot_and_cosine_and_euclidean()
    {
        var a = new float[768];
        var b = new float[768];
        VectorScorer.Dot(a, b).Should().Be(0f);
        VectorScorer.Cosine(a, b).Should().Be(0f); // denom == 0 short-circuit
        VectorScorer.Euclidean(a, b).Should().Be(0f);
    }

    [Fact]
    public void Identical_vectors_have_cosine_one_and_zero_distance()
    {
        var (a, _) = MakePair(768, 42);
        VectorScorer.Cosine(a, a).Should().BeApproximately(1f, 1e-5f);
        VectorScorer.Euclidean(a, a).Should().BeLessThan(1e-3f);
    }

    [Fact]
    public void Antiparallel_vectors_have_cosine_negative_one()
    {
        var (a, _) = MakePair(768, 99);
        var neg = new float[a.Length];
        for (int i = 0; i < a.Length; i++) neg[i] = -a[i];
        VectorScorer.Cosine(a, neg).Should().BeApproximately(-1f, 1e-5f);
    }

    [Fact]
    public void Orthogonal_vectors_have_zero_dot_and_zero_cosine()
    {
        int dim = 128;
        var a = new float[dim];
        var b = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            a[i] = i % 2 == 0 ? 1f : 0f;
            b[i] = i % 2 == 0 ? 0f : 1f;
        }
        VectorScorer.Dot(a, b).Should().Be(0f);
        VectorScorer.Cosine(a, b).Should().Be(0f);
    }

    [Fact]
    public void Constant_vectors_match_scalar()
    {
        int dim = 1536;
        var a = new float[dim];
        var b = new float[dim];
        for (int i = 0; i < dim; i++) { a[i] = 0.5f; b[i] = -0.25f; }
        AssertClose(VectorScorer.Dot(a, b), ScalarVectorScorer.Dot(a, b));
        AssertClose(VectorScorer.Cosine(a, b), ScalarVectorScorer.Cosine(a, b));
        AssertClose(VectorScorer.Euclidean(a, b), ScalarVectorScorer.Euclidean(a, b));
    }
}
