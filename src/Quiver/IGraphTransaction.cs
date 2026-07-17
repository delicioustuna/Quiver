using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// Quiver の最上位グラフトランザクション。Vertex / Edgeの作成・削除、
/// プロパティ操作、隣接列挙、インデックスシーク、物理プラン実行、コミット / ロールバックを
/// 1 つのトランザクション境界として束ねる。
/// </summary>
/// <remarks>
/// <see cref="IDisposable"/> 実装。<see cref="QuiverDatabase.BeginTransaction"/> や
/// <see cref="QuiverDatabase.BeginReadOnlyTransaction"/> で取得し、<c>using</c> で
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

    // ── Vertex操作 ─────────────────────────────────────────────

    /// <summary>指定ラベル名で新規Vertexを作成し、その ID を返す。</summary>
    VertexId CreateVertex(string label);

    /// <summary>指定ラベル ID で新規Vertexを作成し、その ID を返す。</summary>
    VertexId CreateVertex(LabelId labelId);

    /// <summary>指定 ID のVertexを削除する。</summary>
    void DeleteVertex(VertexId vertexId);

    /// <summary>指定 ID のVertexが存在するかを返す。</summary>
    bool VertexExists(VertexId vertexId);

    /// <summary>指定Vertexのラベル名を返す。Vertexが存在しない場合は <c>null</c>。</summary>
    string? GetVertexLabel(VertexId vertexId);

    /// <summary>
    /// Cypher の <c>MERGE (n:label {matchKey: matchValue})</c> 相当 —
    /// <paramref name="label"/> を持ち、<paramref name="matchKey"/> が
    /// <paramref name="matchValue"/> と等しいVertexが存在すればその ID を返す。
    /// 存在しなければ新規Vertexを確保してマッチプロパティをセットし、その ID を返す。
    /// <c>Created</c> でどちらの経路かを判別できるため、呼び出し側で
    /// <c>ON CREATE SET</c> / <c>ON MATCH SET</c> の分岐が書ける。
    /// 重複保持時は VertexId 順で最初にヒットしたものを採用。
    /// 等値判定は String / Bytes はバイト単位、Double はビット完全一致、
    /// Bool / Int32 / Int64 はスカラ等値。
    /// </summary>
    /// <remarks>
    /// パフォーマンス: <c>(label, matchKey)</c> に <see cref="ISchemaApi.CreateIndex"/>
    /// で登録されたインデックスがあれば自動で O(log n) シーク経路を使い、新規作成時の
    /// インデックスエントリ追加も自動で行う。インデックス未登録の場合はラベル内全Vertexに
    /// 対する O(N) フルスキャン + プロパティ比較に落ち、初回呼び出しで
    /// <c>System.Diagnostics.Trace.TraceWarning</c> が出力される (サイレント劣化検出用)。
    /// </remarks>
    (VertexId Id, bool Created) MergeVertex(string label, string matchKey, in PropertyValue matchValue);

    /// <summary>Edge型 ID から型名を返す。未登録 ID では <c>null</c>。</summary>
    string? GetEdgeTypeName(EdgeTypeId typeId);

    // ── リレーション操作 ──────────────────────────────────────

    /// <summary><paramref name="source"/> から <paramref name="target"/> へ指定型のEdgeを作成する。</summary>
    EdgeId CreateEdge(VertexId source, VertexId target, string type);

    /// <summary>型 ID 指定版の <see cref="CreateEdge(VertexId, VertexId, string)"/>。</summary>
    EdgeId CreateEdge(VertexId source, VertexId target, EdgeTypeId typeId);

    /// <summary>
    /// エッジ版 MERGE / UPSERT — <paramref name="source"/> から <paramref name="target"/> へ向かう
    /// <paramref name="type"/> 型のEdgeが既に存在すればその ID を返し、無ければ新規作成して
    /// その ID を返す。<c>Created</c> でどちらの経路かを判別できる (<see cref="MergeVertex"/> と対称)。
    /// 同一 (source, target, type) のエッジが複数あるときは最初にヒットしたものを採用する。
    /// </summary>
    /// <remarks>
    /// 存在判定は <paramref name="source"/> の外向き隣接を走査するため計算量は O(source の out-degree)。
    /// 高 fan-out Vertexで多用する場合はコストに留意すること (エッジ存在インデックスは持たない)。
    /// read-your-writes により、同一トランザクション内で直前に作成したエッジも検出される。
    /// </remarks>
    (EdgeId Id, bool Created) MergeEdge(VertexId source, VertexId target, string type);

    /// <summary>指定 ID のEdgeを削除する。</summary>
    void DeleteEdge(EdgeId edgeId);

    // ── プロパティ操作 ────────────────────────────────────────

    /// <summary>Vertexにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(VertexId vertexId, string key, in PropertyValue value);

    /// <summary>Edgeにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(EdgeId edgeId, string key, in PropertyValue value);

    /// <summary>Vertexからプロパティを削除する。</summary>
    void RemoveProperty(VertexId vertexId, string key);

    /// <summary>Vertexのプロパティ値を取得する。存在しない場合の挙動は実装依存。</summary>
    PropertyValue GetProperty(VertexId vertexId, string key);

    /// <summary>Edgeのプロパティ値を取得する。</summary>
    PropertyValue GetProperty(EdgeId edgeId, string key);

    /// <summary>Vertexが指定キーのプロパティを保持しているかを返す。</summary>
    bool HasProperty(VertexId vertexId, string key);

    // ── マルチバリュープロパティ操作 (Set cardinality) ──────────────────

    /// <summary>
    /// Set cardinality プロパティに値を追加する。同一 key+value が既に存在すればスキップ (冪等)。
    /// Single cardinality キーに対して呼ぶと <see cref="InvalidOperationException"/>。
    /// </summary>
    void AddPropertyValue(VertexId vertexId, string key, in PropertyValue value);

    /// <inheritdoc cref="AddPropertyValue(VertexId, string, in PropertyValue)"/>
    void AddPropertyValue(EdgeId edgeId, string key, in PropertyValue value);

    /// <summary>
    /// Set cardinality プロパティから特定の値を除去する。一致する値が無ければ no-op (冪等)。
    /// Single cardinality キーに対して呼ぶと <see cref="InvalidOperationException"/>。
    /// </summary>
    void RemovePropertyValue(VertexId vertexId, string key, in PropertyValue value);

    /// <inheritdoc cref="RemovePropertyValue(VertexId, string, in PropertyValue)"/>
    void RemovePropertyValue(EdgeId edgeId, string key, in PropertyValue value);

    /// <summary>
    /// Set cardinality プロパティの全値を列挙する。
    /// </summary>
    PropertyValuesEnumerator GetPropertyValues(VertexId vertexId, string key);

    /// <inheritdoc cref="GetPropertyValues(VertexId, string)"/>
    PropertyValuesEnumerator GetPropertyValues(EdgeId edgeId, string key);

    /// <summary>Vertexに付与された全プロパティを列挙する。</summary>
    PropertyCursor EnumerateProperties(VertexId vertexId);

    // ── トラバーサル ──────────────────────────────────────

    /// <summary>
    /// 指定Vertexに接続するEdgeを列挙する。
    /// <paramref name="direction"/> と <paramref name="typeFilter"/> で絞り込み可能。
    /// </summary>
    EdgeEnumerator EnumerateEdges(
        VertexId vertexId,
        Direction direction = Direction.Both,
        string? typeFilter = null);

    // ── インデックス書き込み (データ投入時に手動で呼ぶ) ──────────────

    /// <summary>文字列キーで指定Vertexをインデックスに登録する。</summary>
    void IndexInsert(string indexName, string key, VertexId vertexId);

    /// <summary><see cref="long"/> キーで指定Vertexをインデックスに登録する。</summary>
    void IndexInsert(string indexName, long key, VertexId vertexId);

    /// <summary><see cref="double"/> キーで指定Vertexをインデックスに登録する。</summary>
    void IndexInsert(string indexName, double key, VertexId vertexId);

    // ── インデックスシーク ──────────────────────────────────

    /// <summary>等値シーク。物理プラン経由の利用も可能。</summary>
    VertexIdEnumerator SeekIndex(string indexName, in PropertyValue key);

    /// <summary>範囲シーク。両端の包含有無を指定できる。</summary>
    VertexIdEnumerator RangeIndex(
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

    // ── Nexus操作 ──────────────────────────────────────

    /// <summary>
    /// 指定型と参加メンバーでNexusを作成し、その ID を返す。
    /// メンバーは 2 件以上必要。同じ (Role, VertexId) の組の重複は許可しない。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// arity が 2 未満、role/type が空文字列、同じ (Role, VertexId) の組が重複、
    /// または参照先Vertexが存在しない場合。
    /// </exception>
    NexusId CreateNexus(string type, ReadOnlySpan<NexusMember> members);

    /// <summary>型 ID 指定版の <see cref="CreateNexus(string, ReadOnlySpan{NexusMember})"/>。</summary>
    NexusId CreateNexus(NexusTypeId typeId, ReadOnlySpan<NexusMember> members);

    /// <summary>
    /// Nexusを論理削除する。存在しない ID や削除済み ID は no-op。
    /// </summary>
    void DeleteNexus(NexusId nexusId);

    /// <summary>
    /// Nexusのメンバーを列挙する。<paramref name="role"/> を指定すると
    /// そのロールのメンバーのみに絞り込む。Nexusが不可視な場合は空列挙を返す。
    /// </summary>
    NexusMemberEnumerator GetMembers(NexusId nexusId, string? role = null);

    /// <summary>
    /// 指定Vertexが参加するNexusを列挙する。型やロールで絞り込み可能。
    /// 同一Nexusに複数ロールで参加している場合も重複なく列挙される。
    /// </summary>
    NexusIdEnumerator GetNexuses(VertexId vertexId, string? type = null, string? role = null);

    /// <summary>Nexus型 ID から型名を返す。未登録 ID では <c>null</c>。</summary>
    string? GetNexusTypeName(NexusTypeId typeId);

    // ── Nexusプロパティ操作 ──────────────────────────────

    /// <summary>Nexusにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(NexusId nexusId, string key, in PropertyValue value);

    /// <summary>Nexusのプロパティ値を取得する。存在しない場合は既定値を返す。</summary>
    PropertyValue GetProperty(NexusId nexusId, string key);

    /// <summary>Nexusが指定キーのプロパティを保持しているかを返す。</summary>
    bool HasProperty(NexusId nexusId, string key);

    /// <summary>Nexusからプロパティを削除する。</summary>
    void RemoveProperty(NexusId nexusId, string key);

    /// <summary>Nexusに付与された全プロパティを列挙する。</summary>
    PropertyCursor EnumerateProperties(NexusId nexusId);

    /// <inheritdoc cref="AddPropertyValue(VertexId, string, in PropertyValue)"/>
    void AddPropertyValue(NexusId nexusId, string key, in PropertyValue value);

    /// <inheritdoc cref="RemovePropertyValue(VertexId, string, in PropertyValue)"/>
    void RemovePropertyValue(NexusId nexusId, string key, in PropertyValue value);

    /// <inheritdoc cref="GetPropertyValues(VertexId, string)"/>
    PropertyValuesEnumerator GetPropertyValues(NexusId nexusId, string key);

    // 物理プラン実行 (Execute/ExecuteCursor)、access methods (Access)、隣接ブロック
    // (AdjacencySegments) は内部実装型を露出するため公開面から除外し、internal な
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

/// <summary>Nexusを構成する 1 メンバー (ロール名と参加Vertexの組)。</summary>
public readonly record struct NexusMember(string Role, VertexId VertexId);

/// <summary>
/// Nexusのメンバーを列挙する ref struct 列挙子。
/// ロールフィルタ付きの場合は一致するロールのメンバーのみを返す。
/// </summary>
public ref struct NexusMemberEnumerator
{
    private NexusIncidenceEnumerator _inner;
    private readonly ITokenStore<RoleId> _roleTokens;
    private readonly IVertexStore _vertices;
    private readonly RoleId _roleFilter;
    private NexusMember _current;
    private TransactionUsageGuard? _usageGuard;

    internal NexusMemberEnumerator(
        NexusIncidenceEnumerator inner,
        ITokenStore<RoleId> roleTokens,
        IVertexStore vertices,
        RoleId roleFilter)
    {
        _inner = inner;
        _roleTokens = roleTokens;
        _vertices = vertices;
        _roleFilter = roleFilter;
        _current = default;
        _usageGuard = null;
    }

    internal void AttachUsage(TransactionUsageLease usage)
    {
        _usageGuard = usage.Guard;
        usage.Dispose();
    }

    /// <inheritdoc />
    public bool MoveNext()
    {
        using var usage = _usageGuard?.Enter() ?? default;
        if (_roleTokens is null) return false;
        while (_inner.MoveNext())
        {
            var inc = _inner.Current;
            if (_roleFilter.IsValid && inc.RoleId != _roleFilter)
                continue;
            var materializer = new EntityIdentityMaterializer(_vertices);
            if (!materializer.TryVertex(inc.VertexId, out var member))
                continue;
            _current = new NexusMember(
                _roleTokens.GetName(inc.RoleId),
                member);
            return true;
        }
        return false;
    }

    /// <inheritdoc />
    public NexusMember Current => _current;
    /// <inheritdoc />
    public void Dispose()
    {
        _inner.Dispose();
    }
}

/// <summary>
/// 指定Vertexが参加するNexus ID を列挙する ref struct 列挙子。
/// 型・ロールフィルタ付きの場合は一致するもののみを返す。
/// 同一Nexusに複数ロールで参加している場合も重複なく列挙する。
/// </summary>
public ref struct NexusIdEnumerator
{
    private VertexIncidenceEnumerator _inner;
    private readonly INexusStore _nexuses;
    private readonly NexusTypeId _typeFilter;
    private readonly RoleId _roleFilter;
    private HashSet<long>? _seen;
    private NexusId _current;
    private TransactionUsageGuard? _usageGuard;

    internal NexusIdEnumerator(
        VertexIncidenceEnumerator inner,
        INexusStore nexuses,
        NexusTypeId typeFilter,
        RoleId roleFilter)
    {
        _inner = inner;
        _nexuses = nexuses;
        _typeFilter = typeFilter;
        _roleFilter = roleFilter;
        _seen = null;
        _current = default;
        _usageGuard = null;
    }

    internal void AttachUsage(TransactionUsageLease usage)
    {
        _usageGuard = usage.Guard;
        usage.Dispose();
    }

    /// <inheritdoc />
    public bool MoveNext()
    {
        using var usage = _usageGuard?.Enter() ?? default;
        if (_nexuses is null) return false;
        while (_inner.MoveNext())
        {
            var inc = _inner.Current;
            if (_roleFilter.IsValid && inc.RoleId != _roleFilter)
                continue;
            using var header = _nexuses.Read(inc.NexusId);
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
    public NexusId Current => _current;
    /// <inheritdoc />
    public void Dispose()
    {
        _inner.Dispose();
    }
}

/// <summary><c>long</c> 列挙子を <see cref="VertexId"/> に変換するための薄いラッパ。</summary>
public ref struct VertexIdEnumerator
{
    private IEnumerator<long>? _inner;
    private VertexId _current;
    private TransactionUsageGuard? _usageGuard;

    internal VertexIdEnumerator(IEnumerable<long> source)
    {
        _inner = source.GetEnumerator();
        _current = default;
        _usageGuard = null;
    }

    internal VertexIdEnumerator(IEnumerable<long> source, TransactionUsageLease usage)
        : this(source)
    {
        _usageGuard = usage.Guard;
        usage.Dispose();
    }

    /// <summary>次の要素に進む。要素が無くなったら <c>false</c>。</summary>
    public bool MoveNext()
    {
        using var usage = _usageGuard?.Enter() ?? default;
        if (_inner == null || !_inner.MoveNext())
            return false;
        _current = new VertexId(_inner.Current);
        return true;
    }

    /// <summary>直近の <see cref="MoveNext"/> で取得した現在要素。</summary>
    public VertexId Current => _current;

    /// <summary>内部列挙子を破棄する。</summary>
    public void Dispose()
    {
        using var usage = _usageGuard?.Enter() ?? default;
        _inner?.Dispose();
    }
}
