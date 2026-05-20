using Quiver.Client.Internal;
using Quiver.Client.Match;
using Quiver.Core;

namespace Quiver.Client;

/// <summary>
/// Gremlin 風のグラフトラバーサルを構築するエントリポイント。
/// <see cref="GraphTransactionExtensions.G"/> 拡張で取得し、ノード追加・
/// リレーション追加・スキャン起点・Match DSL・KNN 検索の起点として用いる。
/// </summary>
/// <remarks>
/// 同一トランザクション中で複数のトラバーサルを並行して生成できるが、
/// インスタンス自体はスレッドセーフではない。トランザクション境界を越えて
/// 共有しないこと。
/// </remarks>
public sealed class GraphTraversalSource
{
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;
    // VEC-10: 任意で注入された GraphStats。PendingKnnBuilder.Materialize 経由で
    // graph-first push-down を label cardinality 30% 以上で vector-first フォールバックさせる。
    // null のときは VEC-9 動作 (構造ヒントのみで判定)。
    private readonly GraphStats? _stats;

    /// <summary>
    /// 指定したトランザクションとスキーマでトラバーサルソースを生成する。
    /// 通常は <see cref="GraphTransactionExtensions.G"/> 経由で呼び出す。
    /// </summary>
    /// <param name="tx">所属するグラフトランザクション。</param>
    /// <param name="schema">ラベル / プロパティキー / リレーションシップ型を解決するスキーマ API。</param>
    public GraphTraversalSource(IGraphTransaction tx, ISchemaApi schema)
        : this(tx, schema, stats: null)
    {
    }

    /// <summary>
    /// VEC-10: GraphStats を注入してトラバーサルソースを生成する。
    /// 後段 <c>g.Knn(...).HasLabel(L)</c> 形式の push-down リライト時に、
    /// label cardinality が <see cref="Internal.PendingKnnBuilder.VectorFirstLabelFraction"/>
    /// (既定 30%) 以上のときに vector-first フォールバックを選ぶための判定材料となる。
    /// stats を渡さない場合は VEC-9 と同じ構造ヒントのみで graph-first を選ぶ。
    /// </summary>
    public GraphTraversalSource(IGraphTransaction tx, ISchemaApi schema, GraphStats? stats)
    {
        _tx = tx; _schema = schema; _stats = stats;
    }

    // ── ノード書き込み ──────────────────────────────────────────────────────

    /// <summary>
    /// 新しいノードビルダを開始する。
    /// <c>g.AddNode("Person").P("Name", "Alice").Next()</c> のように呼ぶ。
    /// </summary>
    /// <param name="label">作成するノードのラベル名。</param>
    public NodeBuilder         AddNode(string label) => new(_tx, label);

    /// <summary>
    /// 新しいリレーションシップビルダを開始する。
    /// <c>g.AddRelationship("KNOWS").From(a).To(b).Next()</c> のように呼ぶ。
    /// </summary>
    /// <param name="type">作成するリレーションシップの型名。</param>
    public RelationshipBuilder AddRelationship(string type) => new(_tx, type);

    /// <summary>
    /// GC-5: Cypher の <c>MERGE (n:label {matchKey: matchValue})</c> に相当する糖衣構文。
    /// <see cref="IGraphTransaction.MergeNode"/> のラッパで、<c>Created</c> フラグを
    /// 用いて ON CREATE SET / ON MATCH SET の分岐を呼び出し側で書ける。
    /// </summary>
    /// <param name="label">マージ対象ノードのラベル。</param>
    /// <param name="matchKey">マッチに用いるプロパティキー。</param>
    /// <param name="matchValue">マッチに用いるプロパティ値。</param>
    /// <returns>マッチした or 作成されたノード ID と、新規作成だったかを表すフラグの組。</returns>
    public (NodeId Id, bool Created) MergeNode(string label, string matchKey, in Stores.PropertyValue matchValue)
        => _tx.MergeNode(label, matchKey, in matchValue);

    // ── エンティティ操作糖衣 (IGraphNode<T> ベース) ─────────────────────────

    /// <summary><see cref="IGraphNode{T}"/> 実装型を用いた型安全な Insert。</summary>
    public NodeId Insert<T>(T entity)             where T : IGraphNode<T> => T.Insert(_tx, entity);

    /// <summary><see cref="IGraphNode{T}"/> 実装型を用いた Insert + インデックス登録。</summary>
    public NodeId InsertIndexed<T>(T entity)      where T : IGraphNode<T> => T.InsertIndexed(_tx, entity);

    /// <summary>指定 ID のノードプロパティを <typeparamref name="T"/> インスタンスに復元する。</summary>
    public T      Load<T>(NodeId id)              where T : IGraphNode<T> => T.Load(_tx, id);

    /// <summary>指定 ID のノードのプロパティを <paramref name="entity"/> の値で上書きする。</summary>
    public void   Update<T>(NodeId id, T entity)  where T : IGraphNode<T> => T.Update(_tx, id, entity);

    /// <summary>指定 ID のノードを削除する。</summary>
    public void   Delete<T>(NodeId id)            where T : IGraphNode<T> => T.Delete(_tx, id);

    // ── スキャン起点 ─────────────────────────────────────────────────────────

    /// <summary>全ノードをスキャン起点とするトラバーサルを生成する (Gremlin の <c>g.V()</c> 相当)。</summary>
    public GraphTraversal<NodeId> Nodes()
    {
        var builder = new ScanBuilder();
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>指定 ID のノード 1 件だけを起点とするトラバーサル (Gremlin の <c>g.V(id)</c> 相当)。</summary>
    public GraphTraversal<NodeId> Node(NodeId nodeId)
    {
        var builder = new SingleNodeBuilder(nodeId);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    /// <summary>指定 ID のノード群を起点とするトラバーサル (Gremlin の <c>g.V(ids)</c> 相当)。</summary>
    public GraphTraversal<NodeId> Nodes(params NodeId[] nodeIds)
    {
        var builder = new MultiNodeBuilder(nodeIds);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }

    // ── 型付きスキャン起点 ────────────────────────────────────────────────────

    /// <summary>
    /// <typeparamref name="T"/> の <see cref="IGraphNode{T}.GraphLabel"/> でフィルタした
    /// 型付きトラバーサルを生成する。<c>Has(p => p.Name, "Alice")</c> のような
    /// 式ツリーベースのプロパティ参照が利用可能になる。
    /// </summary>
    public TypedGraphTraversal<T> Nodes<T>() where T : IGraphNode<T>
    {
        var inner = Nodes().HasLabel(T.GraphLabel);
        return new TypedGraphTraversal<T>(inner, _tx, _schema);
    }

    // ── Match DSL ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Match DSL クエリを開始する。<see cref="GraphPattern"/> でパターンを構築し、
    /// <c>Where</c> / <c>Return</c> をチェーンして結果を取得する。
    /// </summary>
    /// <param name="pattern">マッチするノード / エッジパターン。</param>
    public MatchQuery Match(GraphPattern pattern) => new(_tx, _schema, pattern);

    // ── VEC-5: KNN スキャン起点 ────────────────────────────────────────────────

    /// <summary>
    /// ベクトル類似度上位 k 件をスキャン起点とするトラバーサル。
    /// 類似度の降順でノード ID を放出し、<c>.HasLabel(...)</c> や <c>.Out(...)</c> を
    /// 続けて KNN とグラフトラバーサルを組み合わせられる (codex_advice_3.md 6.4 節)。
    /// </summary>
    /// <remarks>
    /// 類似度スコア自体は伝播しない。生スコアが必要な場合は
    /// <c>db.Vectors.KnnSearch(...)</c> を直接呼び出すこと。
    /// インデックスは <see cref="Core.EntityKind.Node"/> にバインドされている必要がある。
    /// リレーションシップ向け KNN は具体的なユースケースが出るまで意図的にスコープ外とする。
    /// <para>
    /// VEC-9: <c>g.Knn(...).HasLabel(...).Has(...)</c> のような後続 pure-filter チェーンは
    /// 自動的に candidate-side に巻き戻され、<see cref="GraphTraversal{T}.FilterByKnn"/> 相当の
    /// graph-first プランに変換される。フィルタが小さい場合は数倍〜数十倍高速化される。
    /// 明示的な graph-first 制御が必要な場合のみ <see cref="GraphTraversal{T}.FilterByKnn"/> を直接呼ぶ。
    /// </para>
    /// <para>
    /// VEC-9: <c>g.Knn(idx, q, k).Limit(n)</c> で <c>n &lt; k</c> のとき、KNN の k を <c>min(k, n)</c> に
    /// 縮めて実行する (後段 filter は candidate-side 処理済のため安全)。
    /// </para>
    /// </remarks>
    /// <param name="indexName">対象のベクトルインデックス名。</param>
    /// <param name="query">問い合わせベクトル。</param>
    /// <param name="k">取得する上位件数。</param>
    public GraphTraversal<NodeId> Knn(string indexName, ReadOnlySpan<float> query, int k)
    {
        // VEC-9: PendingKnnBuilder で包み、後続の pure-filter / Limit を candidate-side に
        // 巻き戻せるようにする。filter が積まれなければ terminal で vector-first に materialize される。
        // VEC-10: _stats を引き継ぎ、Materialize 経路で label cardinality fallback を効かせる。
        // VEC-12: backend が capability 経路で spec を返せれば dim を解決し、PendingKnnBuilder に
        //         dim-aware piecewise threshold を引かせる。spec を返さない backend では dim=0 で
        //         legacy 30% 単一閾値経路に倒れる (HasFastLabelIndex 経路は使われない)。
        int dim = _tx.Access.TryGetVectorIndexSpec(indexName, out var spec) ? spec.Dimensions : 0;
        var builder = new Internal.PendingKnnBuilder(new Internal.ScanBuilder(), indexName, query, k, dim);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0, aliases: null, stats: _stats);
    }
}

/// <summary><see cref="IGraphTransaction"/> から <see cref="GraphTraversalSource"/> を取得する拡張メソッド。</summary>
public static class GraphTransactionExtensions
{
    /// <summary>
    /// トランザクションとスキーマから新規 <see cref="GraphTraversalSource"/> を構築する。
    /// </summary>
    public static GraphTraversalSource G(this IGraphTransaction tx, ISchemaApi schema)
        => new(tx, schema);

    /// <summary>
    /// VEC-10: GraphStats を渡してトラバーサルソースを構築する。
    /// <c>g.Knn(...).HasLabel(L)</c> 形式の push-down が、L の cardinality が高いときに
    /// vector-first にフォールバックして wall-clock 劣化を回避できる。stats を渡さない場合
    /// は VEC-9 と同じ構造ヒントのみで graph-first を選ぶ。
    /// </summary>
    public static GraphTraversalSource G(this IGraphTransaction tx, ISchemaApi schema, GraphStats? stats)
        => new(tx, schema, stats);
}
