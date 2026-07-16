using Quiver.Core;

namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// seed 駆動で再現可能なワークロードを生成する。
///
/// 1 シナリオ = 複数 tx。各 tx は 1〜4 個の <see cref="WorkloadOp"/> (CreateVertex +
/// 0..N 個の SetProperty / IndexInsert) を含み、最後に Commit or Rollback で締める。
/// RNG seed が同じなら必ず同じシーケンスが生成される (debug 用にトレースから再現可能)。
///
/// 値域は意図的に狭く取り、index entry が collide しやすい (= MERGE-like 衝突や
/// 削除後 ID 再利用が観測される) ように調整されている。
/// </summary>
internal static class WorkloadGenerator
{
    public const string IndexName = "idx_chaos_key";

    /// <summary>
    /// <paramref name="seed"/> から <paramref name="txCount"/> 件の transaction を生成する。
    /// それぞれ 80% で commit、20% で rollback。
    /// </summary>
    public static List<WorkloadTx> Generate(int seed, int txCount)
    {
        var rng = new Random(seed);
        var result = new List<WorkloadTx>(txCount);
        int nextKey = 1;
        for (int i = 0; i < txCount; i++)
        {
            int opsInTx = 1 + rng.Next(4); // 1..4
            var ops = new List<WorkloadOp>(opsInTx);
            for (int j = 0; j < opsInTx; j++)
            {
                // 各 op: CreateVertex(label) + 最大 2 件のプロパティ + 50% で IndexInsert
                string label = "L" + (rng.Next(3));
                long propValue = rng.Next(0, 100);
                int keyForIndex = nextKey++;
                bool addIndex = rng.NextDouble() < 0.5;
                ops.Add(new WorkloadOp(label, propValue, addIndex ? keyForIndex : (int?)null));
            }
            bool commit = rng.NextDouble() < 0.8;
            result.Add(new WorkloadTx(ops, commit));
        }
        return result;
    }
}

/// <summary>1 トランザクション分のワークロード。</summary>
internal sealed record WorkloadTx(IReadOnlyList<WorkloadOp> Ops, bool Commit);

/// <summary>1 Vertex作成 + プロパティ + 任意の索引エントリ。</summary>
/// <param name="Label">CreateVertex に渡すラベル名。</param>
/// <param name="PropertyValue">"marker" プロパティに入れる Int64 値。</param>
/// <param name="IndexKey">非 null のとき <see cref="WorkloadGenerator.IndexName"/> へ Int64 キーで insert。</param>
internal sealed record WorkloadOp(string Label, long PropertyValue, int? IndexKey);
