using Quiver.Core;
using Quiver.Api;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// 開始時点のスナップショットを読むトランザクション。
/// entity/property read、query、read-only schema catalog を提供する。
/// </summary>
public interface IReadTransaction : IDisposable
{
    /// <summary>トランザクション識別子。</summary>
    TransactionId Id { get; }

    /// <summary>現在のトランザクション状態。</summary>
    TransactionState State { get; }

    /// <summary>このトランザクションの読み取り専用スキーマカタログ。</summary>
    ISchemaCatalog Schema { get; }

    /// <summary>このトランザクションのスナップショットで query を構築する入口。</summary>
    GraphTraversalSource Query { get; }

    /// <summary>指定 ID のVertexが存在するかを返す。</summary>
    bool VertexExists(VertexId vertexId);

    /// <summary>指定Vertexのラベル名を返す。存在しない場合は <c>null</c>。</summary>
    string? GetVertexLabel(VertexId vertexId);

    /// <summary>Edge型 ID から型名を返す。未登録 ID では <c>null</c>。</summary>
    string? GetEdgeTypeName(EdgeTypeId typeId);

    /// <summary>指定Edgeの型名を返す。存在しない場合は <c>null</c>。</summary>
    string? GetEdgeType(EdgeId edgeId);

    /// <summary>Vertexのプロパティ値を取得する。</summary>
    PropertyValue GetProperty(VertexId vertexId, string key);

    /// <summary>Edgeのプロパティ値を取得する。</summary>
    PropertyValue GetProperty(EdgeId edgeId, string key);

    /// <summary>Nexusのプロパティ値を取得する。</summary>
    PropertyValue GetProperty(NexusId nexusId, string key);

    /// <summary>Vertexが指定キーのプロパティを保持しているかを返す。</summary>
    bool HasProperty(VertexId vertexId, string key);

    /// <summary>Nexusが指定キーのプロパティを保持しているかを返す。</summary>
    bool HasProperty(NexusId nexusId, string key);

    /// <summary>Set cardinality の全値を列挙する。</summary>
    PropertyValuesEnumerator GetPropertyValues(VertexId vertexId, string key);

    /// <summary>Set cardinality の全値を列挙する。</summary>
    PropertyValuesEnumerator GetPropertyValues(EdgeId edgeId, string key);

    /// <summary>Set cardinality の全値を列挙する。</summary>
    PropertyValuesEnumerator GetPropertyValues(NexusId nexusId, string key);

    /// <summary>Vertexに付与された全プロパティを列挙する。</summary>
    PropertyCursor EnumerateProperties(VertexId vertexId);

    /// <summary>Nexusに付与された全プロパティを列挙する。</summary>
    PropertyCursor EnumerateProperties(NexusId nexusId);

    /// <summary>指定Vertexに接続するEdgeを列挙する。</summary>
    EdgeEnumerator EnumerateEdges(
        VertexId vertexId,
        Direction direction = Direction.Both,
        string? typeFilter = null);

    /// <summary>等値 scalar index seek を実行する。</summary>
    EntityRefEnumerator SeekIndex(string indexName, in PropertyValue key);

    /// <summary>scalar index range seek を実行する。</summary>
    EntityRefEnumerator RangeIndex(
        string indexName,
        in PropertyValue from, bool fromInclusive,
        in PropertyValue to, bool toInclusive);

    /// <summary>
    /// 指定 owner の vector property を現在のsnapshotから読み出す。
    /// </summary>
    bool TryGetVectorProperty(EntityRef owner, string propertyKey, Span<float> destination);

    /// <summary>指定vector indexを現在のsnapshotで検索する。</summary>
    VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null);

    /// <summary>複数のquery vectorを同じsnapshotで検索する。</summary>
    IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k,
        VectorSearchOptions? options = null);

    /// <summary>Nexusのメンバーを列挙する。</summary>
    NexusMemberEnumerator GetMembers(NexusId nexusId, string? role = null);

    /// <summary>指定Vertexが参加するNexusを列挙する。</summary>
    NexusIdEnumerator GetNexuses(VertexId vertexId, string? type = null, string? role = null);

    /// <summary>Nexus型 ID から型名を返す。未登録 ID では <c>null</c>。</summary>
    string? GetNexusTypeName(NexusTypeId typeId);

    /// <summary>指定Nexusの型名を返す。存在しない場合は <c>null</c>。</summary>
    string? GetNexusType(NexusId nexusId);
}

/// <summary>
/// 読み取り能力に mutation、schema edit、commit/abort を加えた書き込みトランザクション。
/// </summary>
public interface IWriteTransaction : IReadTransaction, ICommitHookRegistrar
{
    /// <summary>このトランザクションで schema mutation を行う入口。</summary>
    ISchemaEditor EditSchema { get; }

    /// <summary>このトランザクションで graph mutation を構築する入口。</summary>
    GraphMutationSource Mutate { get; }

    /// <summary>指定ラベル名で新規Vertexを作成し、その ID を返す。</summary>
    VertexId CreateVertex(string label);

    /// <summary>指定ラベル ID で新規Vertexを作成し、その ID を返す。</summary>
    VertexId CreateVertex(LabelId labelId);

    /// <summary>指定 ID のVertexを削除する。</summary>
    void DeleteVertex(VertexId vertexId);

    /// <summary>ラベルと scalar property が一致するVertexを返し、無ければ作成する。</summary>
    (VertexId Id, bool Created) MergeVertex(string label, string matchKey, in PropertyValue matchValue);

    /// <summary><paramref name="source"/> から <paramref name="target"/> へ指定型のEdgeを作成する。</summary>
    EdgeId CreateEdge(VertexId source, VertexId target, string type);

    /// <summary>型 ID 指定版の <see cref="CreateEdge(VertexId, VertexId, string)"/>。</summary>
    EdgeId CreateEdge(VertexId source, VertexId target, EdgeTypeId typeId);

    /// <summary>source、type、target が一致するEdgeを返し、無ければ作成する。</summary>
    (EdgeId Id, bool Created) MergeEdge(VertexId source, VertexId target, string type);

    /// <summary>指定 ID のEdgeを削除する。</summary>
    void DeleteEdge(EdgeId edgeId);

    /// <summary>Vertexにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(VertexId vertexId, string key, in PropertyValue value);

    /// <summary>Edgeにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(EdgeId edgeId, string key, in PropertyValue value);

    /// <summary>Vertexからプロパティを削除する。</summary>
    void RemoveProperty(VertexId vertexId, string key);

    /// <summary>Edgeからプロパティを削除する。</summary>
    void RemoveProperty(EdgeId edgeId, string key);

    /// <summary>Set cardinality のVertexプロパティへ値を追加する。</summary>
    void AddPropertyValue(VertexId vertexId, string key, in PropertyValue value);

    /// <inheritdoc cref="AddPropertyValue(VertexId, string, in PropertyValue)"/>
    void AddPropertyValue(EdgeId edgeId, string key, in PropertyValue value);

    /// <summary>Set cardinality のVertexプロパティから値を除去する。</summary>
    void RemovePropertyValue(VertexId vertexId, string key, in PropertyValue value);

    /// <inheritdoc cref="RemovePropertyValue(VertexId, string, in PropertyValue)"/>
    void RemovePropertyValue(EdgeId edgeId, string key, in PropertyValue value);

    /// <summary>
    /// 指定 owner のvector propertyを設定する。
    /// vector indexの有無はpropertyの保存可否に影響しない。
    /// </summary>
    void SetVectorProperty(EntityRef owner, string propertyKey, ReadOnlySpan<float> vector);

    /// <summary>指定型と参加メンバーでNexusを作成する。</summary>
    NexusId CreateNexus(string type, ReadOnlySpan<NexusMember> members);

    /// <summary>型 ID 指定版の <see cref="CreateNexus(string, ReadOnlySpan{NexusMember})"/>。</summary>
    NexusId CreateNexus(NexusTypeId typeId, ReadOnlySpan<NexusMember> members);

    /// <summary>
    /// 型と role 付き member 集合が同じ Nexus を返し、存在しなければ作成する。
    /// member の入力順は同一性に影響しない。
    /// </summary>
    (NexusId Id, bool Created) MergeNexus(
        string type,
        ReadOnlySpan<NexusMember> members);

    /// <summary>Nexusを論理削除する。</summary>
    void DeleteNexus(NexusId nexusId);

    /// <summary>Nexusにプロパティを設定する (既存値は上書き)。</summary>
    void SetProperty(NexusId nexusId, string key, in PropertyValue value);

    /// <summary>Nexusからプロパティを削除する。</summary>
    void RemoveProperty(NexusId nexusId, string key);

    /// <inheritdoc cref="AddPropertyValue(VertexId, string, in PropertyValue)"/>
    void AddPropertyValue(NexusId nexusId, string key, in PropertyValue value);

    /// <inheritdoc cref="RemovePropertyValue(VertexId, string, in PropertyValue)"/>
    void RemovePropertyValue(NexusId nexusId, string key, in PropertyValue value);

    /// <summary>トランザクションをコミットする。</summary>
    void Commit();

    /// <summary>トランザクションをロールバックする。</summary>
    void Rollback();

    /// <summary>トランザクション内に savepoint を作成する。</summary>
    SavepointId Savepoint(string? name = null);

    /// <summary>指定 savepoint 以降の変更を巻き戻す。</summary>
    void RollbackTo(SavepointId savepoint);

    /// <summary>指定 savepoint を解放する。</summary>
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

/// <summary>scalar index が返す型付き entity 参照を列挙する前方列挙子。</summary>
public ref struct EntityRefEnumerator
{
    private IEnumerator<long>? _inner;
    private readonly Func<long, EntityRef?>? _materialize;
    private EntityRef _current;
    private TransactionUsageGuard? _usageGuard;

    internal EntityRefEnumerator(
        IEnumerable<long> source,
        Func<long, EntityRef?> materialize,
        TransactionUsageLease usage)
    {
        _inner = source.GetEnumerator();
        _materialize = materialize;
        _current = default;
        _usageGuard = usage.Guard;
        usage.Dispose();
    }

    /// <summary>次の可視な entity へ進む。候補が尽きたら <c>false</c>。</summary>
    public bool MoveNext()
    {
        using var usage = _usageGuard?.Enter() ?? default;
        while (_inner is not null && _inner.MoveNext())
        {
            EntityRef? materialized = _materialize?.Invoke(_inner.Current);
            if (!materialized.HasValue)
                continue;
            _current = materialized.Value;
            return true;
        }
        return false;
    }

    /// <summary>直近に materialize された entity 参照。</summary>
    public EntityRef Current => _current;

    /// <summary>内部列挙子を破棄する。</summary>
    public void Dispose()
    {
        using var usage = _usageGuard?.Enter() ?? default;
        _inner?.Dispose();
    }
}
