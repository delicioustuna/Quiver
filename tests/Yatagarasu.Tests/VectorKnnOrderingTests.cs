using FluentAssertions;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class VectorKnnOrderingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_offer_permutation_matches_full_score_and_identity_sort(int capacity)
    {
        foreach (float[] scores in new[] { new float[] { 0, 0, 0, 0 }, new float[] { -1, 2, 2, -1 } })
        {
            var values = Enumerable.Range(0, 4).Select(i => new VectorSearchResult(
                EntityRef.From(VertexId.Create(i, 1)), scores[i])).ToArray();
            var expected = values.OrderByDescending(x => x.Score).ThenBy(x => x.EntityId).Take(capacity);
            foreach (var permutation in Permutations(values))
            {
                var heap = new VectorKnnHeap(capacity);
                foreach (var value in permutation) heap.Offer(value);
                heap.ToSortedArray().Should().Equal(expected);
            }
        }
    }

    [Fact]
    public void Non_finite_scores_do_not_displace_finite_candidates()
    {
        var heap = new VectorKnnHeap(2);
        foreach (float score in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1f, -1f })
            heap.Offer(new VectorSearchResult(EntityRef.From(VertexId.Create(0, 1)), score));
        heap.ToSortedArray().Select(x => x.Score).Should().Equal(1f, -1f);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Strict_scoring_rejects_non_finite_values_without_replacing_results(float score)
    {
        var heap = new VectorKnnHeap(1, rejectNonFiniteScores: true);
        var owner = EntityRef.From(VertexId.Create(0, 1));
        heap.Offer(new VectorSearchResult(owner, 1));
        Action invalid = () => heap.Offer(new VectorSearchResult(owner, score));
        invalid.Should().Throw<VectorException>();
        heap.ToSortedArray().Select(x => x.Score).Should().Equal(1f);
    }

    private static IEnumerable<VectorSearchResult[]> Permutations(VectorSearchResult[] values)
    {
        if (values.Length == 0) { yield return []; yield break; }
        for (int i = 0; i < values.Length; i++)
            foreach (var rest in Permutations(values.Where((_, index) => index != i).ToArray()))
                yield return [values[i], .. rest];
    }
}
