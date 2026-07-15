using Quiver.Core;
using Quiver.Storage.Records;
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

    /// <summary>指定ノードのラベル名を返す。ノードが存在しない場合は <c>null</c>。</summary>
    string? GetNodeLabel(NodeId nodeId);

    /// <summary>
    /// Cypher の <c>MERGE (n:label {matchKey: matchValue})</c> 相当 —
    /// <paramref name="label"/> を持ち、<paramref name="matchKey"/> が
    /// <paramref name="matchValue"/> と等しいノードが存在すればその ID を返す。
    /// 存在しなければ新規ノードを確保してマッチプロパティをセットし、その ID を返す。
    /// <c>Created</c> でどちらの経路かを判別できるため、呼び出し側で
    /// <c>ON CREATE SET</c> / <c>ON MATCH SET</c> の分岐が書ける。
    /// 重複保持時は NodeId 順で最初にヒットしたものを採用。
    /// 等値判定は String / Bytes はバイト単位、Double はビット完全一致、
    /// Bool / Int32 / Int64 はスカラ等値。
    /// </summary>
    /// <remarks>
    /// パフォーマンス: <c>(label, matchKey)</c> に <see cref="ISchemaApi.CreateIndex"/>
    /// で登録されたインデックスがあれば自動で O(log n) シーク経路を使い、新規作成時の
    /// インデックスエントリ追加も自動で行う。インデックス未登録の場合はラベル内全ノードに
    /// 対する O(N) フルスキャン + プロパティ比較に落ち、初回呼び出しで
    /// <c>System.Diagnostics.Trace.TraceWarning</c> が出力される (サイレント劣化検出用)。
    /// </remarks>
    (NodeId Id, bool Created) MergeNode(string label, string matchKey, in PropertyValue matchValue);

    /// <summary>リレーションシップ型 ID から型名を返す。未登録 ID では <c>null</c>。</summary>
    string? GetRelationshipTypeName(RelationshipTypeId typeId);

    // ── リレーション操作 ──────────────────────────────────────

    /// <summary><paramref name="source"/> から <paramref name="target"/> へ指定型のリレーションシップを作成する。</summary>
    RelationshipId CreateRelationship(NodeId source, NodeId target, string type);

    /// <summary>型 ID 指定版の <see cref="CreateRelationship(NodeId, NodeId, string)"/>。</summary>
    RelationshipId CreateRelationship(NodeId source, NodeId target, RelationshipTypeId typeId);

    /// <summary>
    /// エッジ版 MERGE / UPSERT — <paramref name="source"/> から <paramref name="target"/> へ向かう
    /// <paramref name="type"/> 型のリレーションシップが既に存在すればその ID を返し、無ければ新規作成して
    /// その ID を返す。<c>Created</c> でどちらの経路かを判別できる (<see cref="MergeNode"/> と対称)。
    /// 同一 (source, target, type) のエッジが複数あるときは最初にヒットしたものを採用する。
    /// </summary>
    /// <remarks>
    /// 存在判定は <paramref name="source"/> の外向き隣接を走査するため計算量は O(source の out-degree)。
    /// 高 fan-out ノードで多用する場合はコストに留意すること (エッジ存在インデックスは持たない)。
    /// read-your-writes により、同一トランザクション内で直前に作成したエッジも検出される。
    /// </remarks>
    (RelationshipId Id, bool Created) MergeRelationship(NodeId source, NodeId target, string type);

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

    // ── マルチバリュープロパティ操作 (Set cardinality) ──────────────────

    /// <summary>
    /// Set cardinality プロパティに値を追加する。同一 key+value が既に存在すればスキップ (冪等)。
    /// Single cardinality キーに対して呼ぶと <see cref="InvalidOperationException"/>。
    /// </summary>
    void AddPropertyValue(NodeId nodeId, string key, in PropertyValue value);

    /// <inheritdoc cref="AddPropertyValue(NodeId, string, in PropertyValue)"/>
    void AddPropertyValue(RelationshipId relId, string key, in PropertyValue value);

    /// <summary>
    /// Set cardinality プロパティから特定の値を除去する。一致する値が無ければ no-op (冪等)。
    /// Single cardinality キーに対して呼ぶと <see cref="InvalidOperationException"/>。
    /// </summary>
    void RemovePropertyValue(NodeId nodeId, string key, in PropertyValue value);

    /// <inheritdoc cref="RemovePropertyValue(NodeId, string, in PropertyValue)"/>
    void RemovePropertyValue(RelationshipId relId, string key, in PropertyValue value);

    /// <summary>
    /// Set cardinality プロパティの全値を列挙する。
    /// </summary>
    PropertyValuesEnumerator GetPropertyValues(NodeId nodeId, string key);

    /// <inheritdoc cref="GetPropertyValues(NodeId, string)"/>
    PropertyValuesEnumerator GetPropertyValues(RelationshipId relId, string key);

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

    // ── インデックスシーク ──────────────────────────────────

    /// <summary>等値シーク。物理プラン経由の利用も可能。</summary>
    NodeIdEnumerator SeekIndex(string indexName, in PropertyValue key);

    /// <summary>範囲シーク。両端の包含有無を指定できる。</summary>
    NodeIdEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive);

    // ── ベクトル (tx 配下) ──────────────────────────────────────

    /// <summary>
    /// このトランザクション境界の内側でベクトルを set / 上書きする。書き込みは
    /// グラフ変更と同じ container WAL に乗り、<see cref="Commit"/> で原子確定、
    /// <see cref="Rollback"/> / クラッシュで巻き戻る (グラフ変更と原子整合)。
    /// バインドキーは <paramref name="entityId"/> の Sequence。
    /// </summary>
    void SetVector(EntityKind kind, long entityId, string indexName, ReadOnlySpan<float> vector)
        => throw new NotSupportedException("This backend does not support transaction-scoped SetVector.");

    /// <summary>
    /// このトランザクション境界の内側でベクトルを論理削除する。原子性は
    /// <see cref="SetVector"/> と同じ。永続化に対応しないバックエンドでは <see cref="NotSupportedException"/>。
    /// </summary>
    void RemoveVector(EntityKind kind, long entityId, string indexName)
        => throw new NotSupportedException("This backend does not support transaction-scoped RemoveVector.");

    /// <summary>
    /// 指定エンティティの格納ベクトルを <paramref name="destination"/> へ読み出す。
    /// alloc-free — 呼び出し側がインデックスの次元数以上のバッファを用意する。
    /// 未設定 / 削除済み / 世代不一致 (slot 再利用による stale binding) は <c>false</c>。
    /// SourceGenerator の <c>float[]</c> プロパティ Load でも内部利用される。
    /// </summary>
    bool TryGetVector(EntityKind kind, long entityId, string indexName, Span<float> destination)
        => throw new NotSupportedException("This backend does not support TryGetVector.");

    // ── ハイパーエッジ操作 ──────────────────────────────────────

    /// <summary>
    /// 指定型と参加メンバーでハイパーエッジを作成し、その ID を返す。
    /// メンバーは 2 件以上必要。同じ (Role, NodeId) の組の重複は許可しない。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// arity が 2 未満、role/type が空文字列、同じ (Role, NodeId) の組が重複、
    /// または参照先ノードが存在しない場合。
    /// </exception>
    HyperedgeId CreateHyperedge(string type, ReadOnlySpan<HyperedgeMember> members);

    /// <summary>型 ID 指定版の <see cref="CreateHyperedge(string, ReadOnlySpan{HyperedgeMember})"/>。</summary>
    HyperedgeId CreateHyperedge(HyperedgeTypeId typeId, ReadOnlySpan<HyperedgeMember> members);

    /// <summary>
    /// ハイパーエッジを論理削除する。存在しない ID や削除済み ID は no-op。
    /// </summary>
    void DeleteHyperedge(HyperedgeId hyperedgeId);

    /// <summary>
    /// ハイパーエッジのメンバーを列挙する。<paramref name="role"/> を指定すると
    /// そのロールのメンバーのみに絞り込む。ハイパーエッジが不可視な場合は空列挙を返す。
    /// </summary>
    HyperedgeMemberEnumerator GetMembers(HyperedgeId hyperedgeId, string? role = null);

    /// <summary>
    /// 指定ノードが参加するハイパーエッジを列挙する。型やロールで絞り込み可能。
    /// 同一ハイパーエッジに複数ロールで参加している場合も重複なく列挙される。
    /// </summary>
    HyperedgeIdEnumerator GetHyperedges(NodeId nodeId, string? type = null, string? role = null);

    /// <summary>ハイパーエッジ型 ID から型名を返す。未登録 ID では <c>null</c>。</summary>
    string? GetHyperedgeTypeName(HyperedgeTypeId typeId);

    // ── ハイパーエッジプロパティ操作 ──────────────────────────────

    /// <summary>ハイパーエッジにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(HyperedgeId hyperedgeId, string key, in PropertyValue value);

    /// <summary>ハイパーエッジのプロパティ値を取得する。存在しない場合は既定値を返す。</summary>
    PropertyValue GetProperty(HyperedgeId hyperedgeId, string key);

    /// <summary>ハイパーエッジが指定キーのプロパティを保持しているかを返す。</summary>
    bool HasProperty(HyperedgeId hyperedgeId, string key);

    /// <summary>ハイパーエッジからプロパティを削除する。</summary>
    void RemoveProperty(HyperedgeId hyperedgeId, string key);

    /// <summary>ハイパーエッジに付与された全プロパティを列挙する。</summary>
    PropertyEnumerator EnumerateProperties(HyperedgeId hyperedgeId);

    /// <inheritdoc cref="AddPropertyValue(NodeId, string, in PropertyValue)"/>
    void AddPropertyValue(HyperedgeId hyperedgeId, string key, in PropertyValue value);

    /// <inheritdoc cref="RemovePropertyValue(NodeId, string, in PropertyValue)"/>
    void RemovePropertyValue(HyperedgeId hyperedgeId, string key, in PropertyValue value);

    /// <inheritdoc cref="GetPropertyValues(NodeId, string)"/>
    PropertyValuesEnumerator GetPropertyValues(HyperedgeId hyperedgeId, string key);

    // 物理プラン実行 (Execute/ExecuteCursor)、access methods (Access)、隣接ブロック
    // (AdjacencyBlocks) は内部実装型を露出するため公開面から除外し、internal な
    // IGraphTransactionInternal へ移設した (利用者は GraphTraversal DSL を使う)。

    /// <summary>トランザクションをコミットする。</summary>
    void Commit();

    /// <summary>トランザクションをロールバックする。</summary>
    void Rollback();

    // ── セーブポイント / 入れ子 undo ───────────────────────────────

    /// <summary>
    /// トランザクション内に savepoint を作成し識別子を返す。
    /// <see cref="RollbackTo"/> でこの時点まで部分的に巻き戻したり、
    /// <see cref="ReleaseSavepoint"/> で親スコープへマージしたりできる。Nested savepoint 可。
    /// </summary>
    /// <remarks>
    /// 部分ロールバックは durable ではない — クラッシュ復旧では tx 全体の commit / abort のみ
    /// 反映され、savepoint 境界は再現されない。長い tx の途中失敗で部分的に巻き戻し
    /// 残りを継続したい運用用途。
    /// </remarks>
    /// <param name="name">診断・例外メッセージ用の任意名。</param>
    SavepointId Savepoint(string? name = null);

    /// <summary>
    /// 指定 savepoint 以降の変更を巻き戻す。Savepoint は消費されず、続けて
    /// 別の変更を行ったあと再度 <see cref="RollbackTo"/> できる。
    /// </summary>
    void RollbackTo(SavepointId savepoint);

    /// <summary>
    /// 指定 savepoint を解放し、その savepoint 以降の変更を親スコープへマージする。
    /// 解放後は当該 SavepointId は無効。
    /// </summary>
    void ReleaseSavepoint(SavepointId savepoint);
}

/// <summary>ハイパーエッジを構成する 1 メンバー (ロール名と参加ノードの組)。</summary>
public readonly record struct HyperedgeMember(string Role, NodeId NodeId);

/// <summary>
/// ハイパーエッジのメンバーを列挙する ref struct 列挙子。
/// ロールフィルタ付きの場合は一致するロールのメンバーのみを返す。
/// </summary>
public ref struct HyperedgeMemberEnumerator
{
    private HyperedgeIncidenceEnumerator _inner;
    private readonly ITokenStore<RoleId> _roleTokens;
    private readonly INodeStore _nodes;
    private readonly RoleId _roleFilter;
    private HyperedgeMember _current;

    internal HyperedgeMemberEnumerator(
        HyperedgeIncidenceEnumerator inner,
        ITokenStore<RoleId> roleTokens,
        INodeStore nodes,
        RoleId roleFilter)
    {
        _inner = inner;
        _roleTokens = roleTokens;
        _nodes = nodes;
        _roleFilter = roleFilter;
        _current = default;
    }

    /// <inheritdoc />
    public bool MoveNext()
    {
        if (_roleTokens is null) return false;
        while (_inner.MoveNext())
        {
            var inc = _inner.Current;
            if (_roleFilter.IsValid && inc.RoleId != _roleFilter)
                continue;
            var materializer = new EntityIdentityMaterializer(_nodes);
            if (!materializer.TryNode(inc.NodeId, out var member))
                continue;
            _current = new HyperedgeMember(
                _roleTokens.GetName(inc.RoleId),
                member);
            return true;
        }
        return false;
    }

    /// <inheritdoc />
    public HyperedgeMember Current => _current;
    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// 指定ノードが参加するハイパーエッジ ID を列挙する ref struct 列挙子。
/// 型・ロールフィルタ付きの場合は一致するもののみを返す。
/// 同一ハイパーエッジに複数ロールで参加している場合も重複なく列挙する。
/// </summary>
public ref struct HyperedgeIdEnumerator
{
    private NodeIncidenceEnumerator _inner;
    private readonly IHyperedgeStore _hyperedges;
    private readonly HyperedgeTypeId _typeFilter;
    private readonly RoleId _roleFilter;
    private HashSet<long>? _seen;
    private HyperedgeId _current;

    internal HyperedgeIdEnumerator(
        NodeIncidenceEnumerator inner,
        IHyperedgeStore hyperedges,
        HyperedgeTypeId typeFilter,
        RoleId roleFilter)
    {
        _inner = inner;
        _hyperedges = hyperedges;
        _typeFilter = typeFilter;
        _roleFilter = roleFilter;
        _seen = null;
        _current = default;
    }

    /// <inheritdoc />
    public bool MoveNext()
    {
        if (_hyperedges is null) return false;
        while (_inner.MoveNext())
        {
            var inc = _inner.Current;
            if (_roleFilter.IsValid && inc.RoleId != _roleFilter)
                continue;
            using var header = _hyperedges.Read(inc.HyperedgeId);
            if (!header.InUse || (_typeFilter.IsValid && header.Type != _typeFilter))
                continue;
            var heId = header.Id;
            _seen ??= new HashSet<long>();
            if (!_seen.Add(heId.Sequence))
                continue;
            _current = heId;
            return true;
        }
        return false;
    }

    /// <inheritdoc />
    public HyperedgeId Current => _current;
    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
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
