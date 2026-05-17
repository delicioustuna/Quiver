using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// Quiver の最上位グラフトランザクション。ノード / リレーションシップの作成・削除、
/// プロパティ操作、隣接列挙、インデックスシーク、物理プラン実行、コミット / ロールバックを
/// 1 つのトランザクション境界として束ねる。
/// </summary>
/// <remarks>
/// <see cref="IDisposable"/> 実装。<see cref="GraphDatabase.BeginTransaction"/> や
/// <see cref="GraphDatabase.BeginReadOnlyTransaction"/> で取得し、<c>using</c> で
/// 確実に破棄すること。スレッドセーフではない (シングルスレッドで利用)。
/// </remarks>
public interface IGraphTransaction : IDisposable, ICommitHookRegistrar
{
    /// <summary>トランザクション識別子。</summary>
    TransactionId Id { get; }

    /// <summary>現在のトランザクション状態 (Active / Committed / RolledBack)。</summary>
    TransactionState State { get; }

    /// <summary>
    /// 読み取り専用としてオープンされたトランザクションの場合に <c>true</c>。
    /// <see cref="Quiver.Operators.ParallelBfsOperator"/> 等の並列トラバーサルオペレータは
    /// 並行書き込み競合を避けるため読み取り専用トランザクションでの実行を必須とする。
    /// </summary>
    bool IsReadOnly { get; }

    // ── ノード操作 ─────────────────────────────────────────────

    /// <summary>指定ラベル名で新規ノードを作成し、その ID を返す。</summary>
    NodeId CreateNode(string label);

    /// <summary>指定ラベル ID で新規ノードを作成し、その ID を返す。</summary>
    NodeId CreateNode(LabelId labelId);

    /// <summary>指定 ID のノードを削除する。</summary>
    void DeleteNode(NodeId nodeId);

    /// <summary>指定 ID のノードが存在するかを返す。</summary>
    bool NodeExists(NodeId nodeId);

    /// <summary>
    /// GC-5: Cypher の <c>MERGE (n:label {matchKey: matchValue})</c> 相当 —
    /// <paramref name="label"/> を持ち、<paramref name="matchKey"/> が
    /// <paramref name="matchValue"/> と等しいノードが存在すればその ID を返す。
    /// 存在しなければ新規ノードを確保してマッチプロパティをセットし、その ID を返す。
    /// <c>Created</c> でどちらの経路かを判別できるため、呼び出し側で
    /// <c>ON CREATE SET</c> / <c>ON MATCH SET</c> の分岐が書ける。
    /// 重複保持時は NodeId 順で最初にヒットしたものを採用。
    /// 等値判定は String / Bytes はバイト単位、Double はビット完全一致、
    /// Bool / Int32 / Int64 はスカラ等値。
    /// </summary>
    (NodeId Id, bool Created) MergeNode(string label, string matchKey, in PropertyValue matchValue);

    // ── リレーション操作 ──────────────────────────────────────

    /// <summary><paramref name="source"/> から <paramref name="target"/> へ指定型のリレーションシップを作成する。</summary>
    RelationshipId CreateRelationship(NodeId source, NodeId target, string type);

    /// <summary>型 ID 指定版の <see cref="CreateRelationship(NodeId, NodeId, string)"/>。</summary>
    RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId);

    /// <summary>指定 ID のリレーションシップを削除する。</summary>
    void DeleteRelationship(RelationshipId relId);

    // ── プロパティ操作 ────────────────────────────────────────

    /// <summary>ノードにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(NodeId nodeId, string key, in PropertyValue value);

    /// <summary>リレーションシップにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(RelationshipId relId, string key, in PropertyValue value);

    /// <summary>ノードからプロパティを削除する。</summary>
    void RemoveProperty(NodeId nodeId, string key);

    /// <summary>ノードのプロパティ値を取得する。存在しない場合の挙動は実装依存。</summary>
    PropertyValue GetProperty(NodeId nodeId, string key);

    /// <summary>リレーションシップのプロパティ値を取得する。</summary>
    PropertyValue GetProperty(RelationshipId relId, string key);

    /// <summary>ノードが指定キーのプロパティを保持しているかを返す。</summary>
    bool HasProperty(NodeId nodeId, string key);

    /// <summary>ノードに付与された全プロパティを列挙する。</summary>
    PropertyEnumerator EnumerateProperties(NodeId nodeId);

    // ── トラバーサル ──────────────────────────────────────

    /// <summary>
    /// 指定ノードに接続するリレーションシップを列挙する。
    /// <paramref name="direction"/> と <paramref name="typeFilter"/> で絞り込み可能。
    /// </summary>
    RelationshipEnumerator EnumerateRelationships(
        NodeId nodeId,
        Direction direction = Direction.Both,
        string? typeFilter = null);

    // ── インデックス書き込み (データ投入時に手動で呼ぶ) ──────────────

    /// <summary>文字列キーで指定ノードをインデックスに登録する。</summary>
    void IndexInsert(string indexName, string key, NodeId nodeId);

    /// <summary><see cref="long"/> キーで指定ノードをインデックスに登録する。</summary>
    void IndexInsert(string indexName, long key, NodeId nodeId);

    /// <summary><see cref="double"/> キーで指定ノードをインデックスに登録する。</summary>
    void IndexInsert(string indexName, double key, NodeId nodeId);

    // ── インデックスシーク (FT-8 公開 API) ──────────────────────

    /// <summary>等値シーク。物理プラン経由の利用も可能。</summary>
    NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key);

    /// <summary>範囲シーク。両端の包含有無を指定できる。</summary>
    NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive);

    /// <summary>
    /// 隣接ブロックインデックス。<see cref="GraphDatabase.BeginBulkLoad"/> を
    /// <c>buildAdjacencyIndex: true</c> で完了させた後に利用可。
    /// </summary>
    IAdjacencyBlockStore? AdjacencyBlocks { get; }

    // ── 物理プラン実行 ────────────────────────────────────

    /// <summary>物理プランを実行して結果を <see cref="QueryResult"/> で返す。</summary>
    QueryResult Execute(IPhysicalOperator plan);

    /// <summary>物理プランをストリーミング実行し、結果を <see cref="IQueryCursor"/> で逐次取得する。</summary>
    IQueryCursor ExecuteCursor(IPhysicalOperator plan);

    /// <summary>トランザクションをコミットする。</summary>
    void Commit();

    /// <summary>トランザクションをロールバックする。</summary>
    void Rollback();
}

/// <summary><c>long</c> 列挙子を <see cref="NodeId"/> に変換するための薄いラッパ。</summary>
public ref struct NodeIdEnumerator
{
    private IEnumerator<long>? _inner;
    private NodeId _current;

    internal NodeIdEnumerator(IEnumerable<long> source)
    {
        _inner = source.GetEnumerator();
        _current = default;
    }

    /// <summary>次の要素に進む。要素が無くなったら <c>false</c>。</summary>
    public bool MoveNext()
    {
        if (_inner == null || !_inner.MoveNext()) return false;
        _current = new NodeId(_inner.Current);
        return true;
    }

    /// <summary>直近の <see cref="MoveNext"/> で取得した現在要素。</summary>
    public NodeId Current => _current;

    /// <summary>内部列挙子を破棄する。</summary>
    public void Dispose() { _inner?.Dispose(); }
}
