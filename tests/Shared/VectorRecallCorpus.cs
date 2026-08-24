namespace Yatagarasu.Testing;

/// <summary>
/// recall ゲートと payload cache 計測が共有する決定的な RAG 向けベクトルコーパス定義。
/// ベンチマークと品質ゲートで seed・分布・クエリ生成を揃える。
/// </summary>
internal static class VectorRecallCorpus
{
    public const int Seed = 98_765;
    public const int RecallDimensions = 384;
    public const int RecallCount = 10_000;
    public const int K = 10;
    public const int QueryCount = 20;

    public static float[] NextVector(Random random, int dimensions)
    {
        var vector = new float[dimensions];
        Fill(random, vector);
        return vector;
    }

    public static void Fill(Random random, Span<float> destination)
    {
        for (int i = 0; i < destination.Length; i++)
            destination[i] = (float)(random.NextDouble() * 2.0 - 1.0);
    }
}
