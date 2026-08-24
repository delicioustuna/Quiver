namespace Yatagarasu;

/// <summary>Source Generator が graph model に実装する型付き mapper 契約。</summary>
/// <typeparam name="TSelf">mapper を持つ graph model 型。</typeparam>
public interface IGraphEntity<TSelf> where TSelf : IGraphEntity<TSelf>
{
    /// <summary>Vertex に割り当てる graph label。</summary>
    static abstract string GraphLabel { get; }

    /// <summary>公開読み取り境界から model を復元する。</summary>
    static abstract TSelf Read(GraphReadAccess read, VertexKey vertex);

    /// <summary>model の全プロパティを公開書き込み境界へ反映する。</summary>
    static abstract void Write(GraphWriteAccess write, VertexKey vertex, TSelf entity);
}

/// <summary>Source Generator が Edge model に実装する型付き mapper 契約。</summary>
/// <typeparam name="TSelf">mapper を持つ Edge model 型。</typeparam>
public interface IGraphEdgeEntity<TSelf> where TSelf : IGraphEdgeEntity<TSelf>
{
    /// <summary>Edge に割り当てる graph type。</summary>
    static abstract string GraphType { get; }

    /// <summary>公開読み取り境界から Edge model を復元する。</summary>
    static abstract TSelf Read(GraphReadAccess read, EdgeKey edge);

    /// <summary>Edge model の全プロパティを公開書き込み境界へ反映する。</summary>
    static abstract void Write(GraphWriteAccess write, EdgeKey edge, TSelf entity);
}

/// <summary>Source Generator が Nexus model に実装する型付き mapper 契約。</summary>
/// <typeparam name="TSelf">mapper を持つ Nexus model 型。</typeparam>
public interface IGraphNexusEntity<TSelf> where TSelf : IGraphNexusEntity<TSelf>
{
    /// <summary>Nexus に割り当てる graph type。</summary>
    static abstract string GraphType { get; }

    /// <summary>Nexus model に束縛された role member を返す。</summary>
    static abstract IReadOnlyList<GraphNexusMember> GetMembers(TSelf entity);

    /// <summary>公開読み取り境界から Nexus model を復元する。</summary>
    static abstract TSelf Read(GraphReadAccess read, NexusKey nexus);

    /// <summary>Nexus model の全プロパティを公開書き込み境界へ反映する。</summary>
    static abstract void Write(GraphWriteAccess write, NexusKey nexus, TSelf entity);
}

/// <summary>型付き Vertex の不透明な参照。</summary>
/// <typeparam name="T">参照先 model 型。</typeparam>
/// <param name="Key">Vertex 識別子。</param>
public readonly record struct GraphEntity<T>(VertexKey Key) where T : IGraphEntity<T>;

/// <summary>型付き Edge の不透明な参照。</summary>
/// <typeparam name="T">Edge model 型。</typeparam>
/// <param name="Key">Edge 識別子。</param>
public readonly record struct GraphEdgeEntity<T>(EdgeKey Key) where T : IGraphEdgeEntity<T>;

/// <summary>型付き Nexus の不透明な参照。</summary>
/// <typeparam name="T">Nexus model 型。</typeparam>
/// <param name="Key">Nexus 識別子。</param>
public readonly record struct GraphNexusEntity<T>(NexusKey Key) where T : IGraphNexusEntity<T>;

/// <summary>同じ label に属する型付き Vertex 集合。</summary>
/// <typeparam name="T">model 型。</typeparam>
public sealed class GraphSet<T> where T : IGraphEntity<T>
{
    internal GraphSet()
    {
    }

    /// <summary>集合が対応する graph label。</summary>
    public string Label => T.GraphLabel;
}

/// <summary>型付き読み取り callback の処理。</summary>
/// <typeparam name="TResult">callback の結果型。</typeparam>
/// <param name="scope">型付き読み取り scope。</param>
/// <returns>callback の結果。</returns>
public delegate TResult TypedGraphReadOperation<out TResult>(TypedGraphReadScope scope);

/// <summary>型付き読み取り callback の処理。</summary>
/// <param name="scope">型付き読み取り scope。</param>
public delegate void TypedGraphReadAction(TypedGraphReadScope scope);

/// <summary>型付き書き込み callback の処理。</summary>
/// <typeparam name="TResult">callback の結果型。</typeparam>
/// <param name="scope">型付き書き込み scope。</param>
/// <returns>callback の結果。</returns>
public delegate TResult TypedGraphWriteOperation<out TResult>(TypedGraphWriteScope scope);

/// <summary>型付き書き込み callback の処理。</summary>
/// <param name="scope">型付き書き込み scope。</param>
public delegate void TypedGraphWriteAction(TypedGraphWriteScope scope);

/// <summary>callback 上で型付き model を読み取る scope。</summary>
public sealed class TypedGraphReadScope
{
    internal TypedGraphReadScope(GraphReadScope raw) => Raw = raw;

    /// <summary>安定した低レベル graph 操作へ降りる入口。</summary>
    public GraphReadAccess Raw { get; }

    /// <summary>指定 model 型の集合を返す。</summary>
    public GraphSet<T> Set<T>() where T : IGraphEntity<T> => new();

    /// <summary>型付き参照から model を復元する。</summary>
    public T Get<T>(GraphEntity<T> entity) where T : IGraphEntity<T> => T.Read(Raw, entity.Key);

    /// <summary>型付き Edge 参照から model を復元する。</summary>
    public T Get<T>(GraphEdgeEntity<T> entity) where T : IGraphEdgeEntity<T> => T.Read(Raw, entity.Key);

    /// <summary>型付き Nexus 参照から model を復元する。</summary>
    public T Get<T>(GraphNexusEntity<T> entity) where T : IGraphNexusEntity<T> => T.Read(Raw, entity.Key);

    /// <summary>型付き参照が現在の snapshot に存在するかを返す。</summary>
    public bool Contains<T>(GraphEntity<T> entity) where T : IGraphEntity<T> => Raw.Contains(entity.Key);

    /// <summary>指定した Edge 型を outgoing に辿り、対象集合の model 参照を返す。</summary>
    public IReadOnlyList<GraphEntity<TTarget>> Related<TSource, TTarget>(
        GraphEntity<TSource> source,
        string edgeType,
        GraphSet<TTarget> targetSet)
        where TSource : IGraphEntity<TSource>
        where TTarget : IGraphEntity<TTarget>
    {
        ArgumentException.ThrowIfNullOrEmpty(edgeType);
        ArgumentNullException.ThrowIfNull(targetSet);
        IReadOnlyList<VertexKey> neighbors = Raw.GetNeighbors(
            source.Key,
            GraphDirection.Outgoing,
            edgeType);
        var result = new List<GraphEntity<TTarget>>(neighbors.Count);
        foreach (VertexKey neighbor in neighbors)
        {
            if (string.Equals(Raw.GetLabel(neighbor), targetSet.Label, StringComparison.Ordinal))
                result.Add(new GraphEntity<TTarget>(neighbor));
        }

        return result;
    }
}

/// <summary>callback 上で型付き model を変更する scope。</summary>
public sealed class TypedGraphWriteScope
{
    internal TypedGraphWriteScope(GraphWriteScope raw) => Raw = raw;

    /// <summary>安定した低レベル graph 操作へ降りる入口。</summary>
    public GraphWriteAccess Raw { get; }

    /// <summary>指定 model 型の集合を返す。</summary>
    public GraphSet<T> Set<T>() where T : IGraphEntity<T> => new();

    /// <summary>model を追加して型付き参照を返す。</summary>
    public GraphEntity<T> Add<T>(GraphSet<T> set, T entity) where T : IGraphEntity<T>
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(entity);
        VertexKey key = Raw.CreateVertex(set.Label);
        T.Write(Raw, key, entity);
        return new GraphEntity<T>(key);
    }

    /// <summary>型付き参照から model を復元する。</summary>
    public T Get<T>(GraphEntity<T> entity) where T : IGraphEntity<T> => T.Read(Raw, entity.Key);

    /// <summary>model を read-modify-write で部分更新し、更新後の値を返す。</summary>
    public T Update<T>(GraphEntity<T> entity, Func<T, T> update) where T : IGraphEntity<T>
    {
        ArgumentNullException.ThrowIfNull(update);
        T current = T.Read(Raw, entity.Key);
        T changed = update(current);
        ArgumentNullException.ThrowIfNull(changed);
        T.Write(Raw, entity.Key, changed);
        return changed;
    }

    /// <summary>型付き Vertex を削除する。</summary>
    public void Delete<T>(GraphEntity<T> entity) where T : IGraphEntity<T> => Raw.Delete(entity.Key);

    /// <summary>二つの型付き Vertex を Edge で接続する。</summary>
    public EdgeKey Connect<TSource, TTarget>(
        GraphEntity<TSource> source,
        string edgeType,
        GraphEntity<TTarget> target)
        where TSource : IGraphEntity<TSource>
        where TTarget : IGraphEntity<TTarget> => Raw.Connect(source.Key, edgeType, target.Key);

    /// <summary>型付き Edge model を伴って二つの Vertex を接続する。</summary>
    public GraphEdgeEntity<TEdge> Connect<TSource, TEdge, TTarget>(
        GraphEntity<TSource> source,
        TEdge edge,
        GraphEntity<TTarget> target)
        where TSource : IGraphEntity<TSource>
        where TEdge : IGraphEdgeEntity<TEdge>
        where TTarget : IGraphEntity<TTarget>
    {
        ArgumentNullException.ThrowIfNull(edge);
        EdgeKey key = Raw.Connect(source.Key, TEdge.GraphType, target.Key);
        TEdge.Write(Raw, key, edge);
        return new GraphEdgeEntity<TEdge>(key);
    }

    /// <summary>型付き Nexus model を追加する。</summary>
    public GraphNexusEntity<TNexus> Add<TNexus>(TNexus entity)
        where TNexus : IGraphNexusEntity<TNexus>
    {
        ArgumentNullException.ThrowIfNull(entity);
        NexusKey key = Raw.CreateNexus(TNexus.GraphType, TNexus.GetMembers(entity));
        TNexus.Write(Raw, key, entity);
        return new GraphNexusEntity<TNexus>(key);
    }
}

/// <summary>Source Generator の mapper を callback scope 上で使用する型付き workspace。</summary>
public sealed class GraphWorkspace : IDisposable
{
    private readonly GraphStore _store;
    private readonly bool _ownsStore;

    private GraphWorkspace(GraphStore store, bool ownsStore)
    {
        _store = store;
        _ownsStore = ownsStore;
    }

    /// <summary>単一の <c>*.yata</c> ファイルを開く。</summary>
    public static GraphWorkspace Open(string filePath, GraphStoreOptions? options = null) =>
        new(GraphStore.Open(filePath, options), ownsStore: true);

    /// <summary>プロセス内 RAM だけを使用する一時 workspace を作成する。</summary>
    public static GraphWorkspace OpenMemory(GraphStoreOptions? options = null) =>
        new(GraphStore.OpenMemory(options), ownsStore: true);

    /// <summary>既存の graph store に、所有権を移さず型付き workspace を重ねる。</summary>
    public static GraphWorkspace Attach(GraphStore store) =>
        new(store ?? throw new ArgumentNullException(nameof(store)), ownsStore: false);

    /// <summary>長時間 snapshot と高度操作の低レベル入口。</summary>
    public AdvancedGraphStore Advanced => _store.Advanced;

    /// <summary>型付き読み取り callback を実行する。</summary>
    public void Read(TypedGraphReadAction operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _store.Read(raw => operation(new TypedGraphReadScope(raw)));
    }

    /// <summary>型付き読み取り callback を実行して結果を返す。</summary>
    public TResult Read<TResult>(TypedGraphReadOperation<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _store.Read(raw => operation(new TypedGraphReadScope(raw)));
    }

    /// <summary>型付き書き込み callback を実行し、正常終了時だけ commit する。</summary>
    public void Write(TypedGraphWriteAction operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _store.Write(raw => operation(new TypedGraphWriteScope(raw)));
    }

    /// <summary>型付き書き込み callback を実行し、正常終了時だけ commit して結果を返す。</summary>
    public TResult Write<TResult>(TypedGraphWriteOperation<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _store.Write(raw => operation(new TypedGraphWriteScope(raw)));
    }

    /// <summary>workspace と下層 store を閉じる。</summary>
    public void Dispose()
    {
        if (_ownsStore)
            _store.Dispose();
    }
}
