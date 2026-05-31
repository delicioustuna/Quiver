using Quiver.Core;

namespace Quiver.Transactions;

/// <summary>
/// FT-33: SSN (Serial Safety Net, Wang et al. DaMoN'15) の per-transaction read/write set。
/// <see cref="IsolationLevel.Serializable"/> のトランザクションでのみ生成される。
///
/// <para>read-set は <see cref="ISsnReadSink"/> 実装として下層ストアの物理読み取り点
/// (Read / Scan / 隣接走査) から <see cref="OnVisibleRead"/> 経由で収集する。これにより
/// 直接 Read だけでなく traversal / scan / index seek 経由の読み取りも漏れなく入る。
/// write-set は <c>TxNodeStore</c> / <c>TxRelationshipStore</c> の write hook が登録する。
/// η(T) / π(T) の計算と exclusion window (π(T) &gt; η(T)) 判定は commit 時に
/// <see cref="TransactionManager"/> の commit-stamp クロックで一括実行する
/// (<c>Transaction.SsnValidateAndStamp</c>)。</para>
///
/// <para>granularity は Node / Relationship 単位 (プロパティ変更は所有ノードの read/write で
/// 捕捉される — per-property より粗いが over-abort 方向で安全)。phantom protection は対象外
/// (index versioning 前提)。</para>
/// </summary>
internal sealed class SsnContext : ISsnReadSink
{
    /// <summary>このトランザクションが読み出したバージョン (Node / Relationship)。</summary>
    public readonly HashSet<EntityId> Reads = new();

    /// <summary>このトランザクションが上書き / 削除したバージョン (Node / Relationship)。</summary>
    public readonly HashSet<EntityId> Writes = new();

    /// <inheritdoc/>
    public void OnVisibleRead(EntityKind kind, long localId)
    {
        if (localId < 0) return;
        var id = new EntityId(kind, localId);
        // 自分が上書き済みのバージョンは read 依存に数えない (self r:w 抹消)。
        if (!Writes.Contains(id)) Reads.Add(id);
    }
}
