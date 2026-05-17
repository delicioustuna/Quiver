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

    /// <summary>
    /// 指定したトランザクションとスキーマでトラバーサルソースを生成する。
    /// 通常は <see cref="GraphTransactionExtensions.G"/> 経由で呼び出す。
    /// </summary>
    /// <param name="tx">所属するグラフトランザクション。</param>
    /// <param name="schema">ラベル / プロパティキー / リレーションシップ型を解決するスキーマ API。</param>
    public GraphTraversalSource(IGraphTransaction tx, ISchemaApi schema)
    {
        _tx = tx; _schema = schema;
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
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0);
    }

    /// <summary>指定 ID のノード 1 件だけを起点とするトラバーサル (Gremlin の <c>g.V(id)</c> 相当)。</summary>
    public GraphTraversal<NodeId> Node(NodeId nodeId)
    {
        var builder = new SingleNodeBuilder(nodeId);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0);
    }

    /// <summary>指定 ID のノード群を起点とするトラバーサル (Gremlin の <c>g.V(ids)</c> 相当)。</summary>
    public GraphTraversal<NodeId> Nodes(params NodeId[] nodeIds)
    {
        var builder = new MultiNodeBuilder(nodeIds);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0);
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
    /// </remarks>
    /// <param name="indexName">対象のベクトルインデックス名。</param>
    /// <param name="query">問い合わせベクトル。</param>
    /// <param name="k">取得する上位件数。</param>
    public GraphTraversal<NodeId> Knn(string indexName, ReadOnlySpan<float> query, int k)
    {
        var builder = new Internal.KnnNodeSourceBuilder(indexName, query, k);
        return new GraphTraversal<NodeId>(_tx, _schema, builder, row => row.GetNodeId(0), 0);
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
}
