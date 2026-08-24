using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu;

/// <summary>読み取り callback の処理。</summary>
/// <param name="scope">callback の間だけ有効な読み取り scope。</param>
public delegate void GraphReadAction(GraphReadScope scope);

/// <summary>結果を返す読み取り callback の処理。</summary>
/// <typeparam name="TResult">callback の結果型。</typeparam>
/// <param name="scope">callback の間だけ有効な読み取り scope。</param>
/// <returns>callback の結果。</returns>
public delegate TResult GraphReadOperation<out TResult>(GraphReadScope scope);

/// <summary>書き込み callback の処理。</summary>
/// <param name="scope">callback の間だけ有効な書き込み scope。</param>
public delegate void GraphWriteAction(GraphWriteScope scope);

/// <summary>結果を返す書き込み callback の処理。</summary>
/// <typeparam name="TResult">callback の結果型。</typeparam>
/// <param name="scope">callback の間だけ有効な書き込み scope。</param>
/// <returns>callback の結果。</returns>
public delegate TResult GraphWriteOperation<out TResult>(GraphWriteScope scope);

/// <summary>読み取り scope と読み取り session が共有する安定した graph 操作。</summary>
public abstract class GraphReadAccess
{
    private bool _active = true;

    private protected GraphReadAccess(IReadTransaction transaction)
    {
        Transaction = transaction;
    }

    internal IReadTransaction Transaction { get; }

    /// <summary>現在の snapshot に束縛された traversal と Match の入口。</summary>
    public GraphQuery Query
    {
        get
        {
            EnsureActive();
            return new GraphQuery(this);
        }
    }

    /// <summary>指定した Vertex が現在の snapshot に存在するかを返す。</summary>
    public bool Contains(VertexKey vertex)
    {
        EnsureActive();
        return Transaction.VertexExists(vertex.ToCore());
    }

    /// <summary>指定した Vertex のラベルを返す。存在しない場合は <c>null</c>。</summary>
    public string? GetLabel(VertexKey vertex)
    {
        EnsureActive();
        return Transaction.GetVertexLabel(vertex.ToCore());
    }

    /// <summary>Vertex のプロパティを取得する。</summary>
    public bool TryGet(VertexKey vertex, string property, out GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        if (!Transaction.HasProperty(vertex.ToCore(), property))
        {
            value = default;
            return false;
        }

        value = GraphValue.FromCore(Transaction.GetProperty(vertex.ToCore(), property));
        return true;
    }

    /// <summary>Edge のプロパティを取得する。</summary>
    public bool TryGet(EdgeKey edge, string property, out GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue current = Transaction.GetProperty(edge.ToCore(), property);
        if ((byte)current.Type == 0)
        {
            value = default;
            return false;
        }

        value = GraphValue.FromCore(current);
        return true;
    }

    /// <summary>Nexus のプロパティを取得する。</summary>
    public bool TryGet(NexusKey nexus, string property, out GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        if (!Transaction.HasProperty(nexus.ToCore(), property))
        {
            value = default;
            return false;
        }

        value = GraphValue.FromCore(Transaction.GetProperty(nexus.ToCore(), property));
        return true;
    }

    /// <summary>Vertex の必須プロパティを取得する。</summary>
    public GraphValue Get(VertexKey vertex, string property) => TryGet(vertex, property, out GraphValue value)
        ? value
        : throw new KeyNotFoundException($"Vertex {vertex} にプロパティ '{property}' はありません。");

    /// <summary>Edge の必須プロパティを取得する。</summary>
    public GraphValue Get(EdgeKey edge, string property) => TryGet(edge, property, out GraphValue value)
        ? value
        : throw new KeyNotFoundException($"Edge {edge} にプロパティ '{property}' はありません。");

    /// <summary>Nexus の必須プロパティを取得する。</summary>
    public GraphValue Get(NexusKey nexus, string property) => TryGet(nexus, property, out GraphValue value)
        ? value
        : throw new KeyNotFoundException($"Nexus {nexus} にプロパティ '{property}' はありません。");

    /// <summary>Vertex の Set cardinality プロパティを所有権付きリストとして返す。</summary>
    public IReadOnlyList<GraphValue> GetValues(VertexKey vertex, string property)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        var result = new List<GraphValue>();
        var cursor = Transaction.GetPropertyValues(vertex.ToCore(), property);
        try
        {
            while (cursor.MoveNext())
                result.Add(GraphValue.FromCore(cursor.Current));
        }
        finally
        {
            cursor.Dispose();
        }

        return result;
    }

    /// <summary>Edge の Set cardinality プロパティを所有権付きリストとして返す。</summary>
    public IReadOnlyList<GraphValue> GetValues(EdgeKey edge, string property)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        var result = new List<GraphValue>();
        var cursor = Transaction.GetPropertyValues(edge.ToCore(), property);
        try
        {
            while (cursor.MoveNext()) result.Add(GraphValue.FromCore(cursor.Current));
        }
        finally
        {
            cursor.Dispose();
        }

        return result;
    }

    /// <summary>Nexus の Set cardinality プロパティを所有権付きリストとして返す。</summary>
    public IReadOnlyList<GraphValue> GetValues(NexusKey nexus, string property)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        var result = new List<GraphValue>();
        var cursor = Transaction.GetPropertyValues(nexus.ToCore(), property);
        try
        {
            while (cursor.MoveNext()) result.Add(GraphValue.FromCore(cursor.Current));
        }
        finally
        {
            cursor.Dispose();
        }

        return result;
    }

    /// <summary>指定した Vertex に接続する Edge を所有権付きリストとして返す。</summary>
    public IReadOnlyList<GraphEdge> GetEdges(
        VertexKey vertex,
        GraphDirection direction = GraphDirection.Both,
        string? type = null)
    {
        EnsureActive();
        var result = new List<GraphEdge>();
        var cursor = Transaction.EnumerateEdges(vertex.ToCore(), direction.ToCore(), type);
        try
        {
            while (cursor.MoveNext())
            {
                EdgeReadHandle edge = cursor.Current;
                string edgeType = Transaction.GetEdgeTypeName(edge.Type) ?? string.Empty;
                result.Add(new GraphEdge(
                    new EdgeKey(edge.Id),
                    new VertexKey(edge.Source),
                    new VertexKey(edge.Target),
                    edgeType));
            }
        }
        finally
        {
            cursor.Dispose();
        }

        return result;
    }

    /// <summary>指定した Vertex の隣接 Vertex を所有権付きリストとして返す。</summary>
    public IReadOnlyList<VertexKey> GetNeighbors(
        VertexKey vertex,
        GraphDirection direction = GraphDirection.Both,
        string? type = null)
    {
        IReadOnlyList<GraphEdge> edges = GetEdges(vertex, direction, type);
        var result = new VertexKey[edges.Count];
        for (int i = 0; i < edges.Count; i++)
            result[i] = edges[i].Source == vertex ? edges[i].Target : edges[i].Source;
        return result;
    }

    /// <summary>指定した Nexus のメンバーを所有権付きリストとして返す。</summary>
    public IReadOnlyList<GraphNexusMember> GetMembers(NexusKey nexus, string? role = null)
    {
        EnsureActive();
        var result = new List<GraphNexusMember>();
        var cursor = Transaction.GetMembers(nexus.ToCore(), role);
        try
        {
            while (cursor.MoveNext())
                result.Add(new GraphNexusMember(cursor.Current.Role, new VertexKey(cursor.Current.VertexId)));
        }
        finally
        {
            cursor.Dispose();
        }

        return result;
    }

    /// <summary>指定した Vertex が参加する Nexus を所有権付きリストとして返す。</summary>
    public IReadOnlyList<NexusKey> GetNexuses(VertexKey vertex, string? type = null, string? role = null)
    {
        EnsureActive();
        var result = new List<NexusKey>();
        var cursor = Transaction.GetNexuses(vertex.ToCore(), type, role);
        try
        {
            while (cursor.MoveNext())
                result.Add(new NexusKey(cursor.Current));
        }
        finally
        {
            cursor.Dispose();
        }

        return result;
    }

    internal void EnsureActive()
    {
        if (!_active)
            throw new ObjectDisposedException(GetType().Name, "Graph access の有効期間は終了しています。");
    }

    internal void Deactivate() => _active = false;
}

/// <summary>書き込み scope と書き込み session が共有する安定した graph 操作。</summary>
public abstract class GraphWriteAccess : GraphReadAccess
{
    private protected GraphWriteAccess(IWriteTransaction transaction)
        : base(transaction)
    {
        WriteTransaction = transaction;
    }

    internal IWriteTransaction WriteTransaction { get; }

    /// <summary>指定したラベルの Vertex を作成する。</summary>
    public VertexKey CreateVertex(string label)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(label);
        return new VertexKey(WriteTransaction.CreateVertex(label));
    }

    /// <summary>Vertex を削除する。</summary>
    public void Delete(VertexKey vertex)
    {
        EnsureActive();
        WriteTransaction.DeleteVertex(vertex.ToCore());
    }

    /// <summary>Edge を削除する。</summary>
    public void Delete(EdgeKey edge)
    {
        EnsureActive();
        WriteTransaction.DeleteEdge(edge.ToCore());
    }

    /// <summary>Nexus を削除する。</summary>
    public void Delete(NexusKey nexus)
    {
        EnsureActive();
        WriteTransaction.DeleteNexus(nexus.ToCore());
    }

    /// <summary>Vertex のプロパティを設定する。</summary>
    public void Set(VertexKey vertex, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.SetProperty(vertex.ToCore(), property, in core);
    }

    /// <summary>Edge のプロパティを設定する。</summary>
    public void Set(EdgeKey edge, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.SetProperty(edge.ToCore(), property, in core);
    }

    /// <summary>Nexus のプロパティを設定する。</summary>
    public void Set(NexusKey nexus, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.SetProperty(nexus.ToCore(), property, in core);
    }

    /// <summary>Vertex のプロパティを削除する。</summary>
    public void Remove(VertexKey vertex, string property)
    {
        EnsureActive();
        WriteTransaction.RemoveProperty(vertex.ToCore(), property);
    }

    /// <summary>Edge のプロパティを削除する。</summary>
    public void Remove(EdgeKey edge, string property)
    {
        EnsureActive();
        WriteTransaction.RemoveProperty(edge.ToCore(), property);
    }

    /// <summary>Nexus のプロパティを削除する。</summary>
    public void Remove(NexusKey nexus, string property)
    {
        EnsureActive();
        WriteTransaction.RemoveProperty(nexus.ToCore(), property);
    }

    /// <summary>Vertex の Set cardinality プロパティへ値を追加する。</summary>
    public void AddValue(VertexKey vertex, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.AddPropertyValue(vertex.ToCore(), property, in core);
    }

    /// <summary>Vertex の Set cardinality プロパティから値を削除する。</summary>
    public void RemoveValue(VertexKey vertex, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.RemovePropertyValue(vertex.ToCore(), property, in core);
    }

    /// <summary>Edge の Set cardinality プロパティへ値を追加する。</summary>
    public void AddValue(EdgeKey edge, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.AddPropertyValue(edge.ToCore(), property, in core);
    }

    /// <summary>Edge の Set cardinality プロパティから値を削除する。</summary>
    public void RemoveValue(EdgeKey edge, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.RemovePropertyValue(edge.ToCore(), property, in core);
    }

    /// <summary>Nexus の Set cardinality プロパティへ値を追加する。</summary>
    public void AddValue(NexusKey nexus, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.AddPropertyValue(nexus.ToCore(), property, in core);
    }

    /// <summary>Nexus の Set cardinality プロパティから値を削除する。</summary>
    public void RemoveValue(NexusKey nexus, string property, GraphValue value)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        PropertyValue core = value.ToCore();
        WriteTransaction.RemovePropertyValue(nexus.ToCore(), property, in core);
    }

    /// <summary>二つの Vertex を Edge で接続する。</summary>
    public EdgeKey Connect(VertexKey source, string type, VertexKey target)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(type);
        return new EdgeKey(WriteTransaction.CreateEdge(source.ToCore(), target.ToCore(), type));
    }

    /// <summary>指定した型とメンバーで Nexus を作成する。</summary>
    public NexusKey CreateNexus(string type, IReadOnlyList<GraphNexusMember> members)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(members);
        var converted = new NexusMember[members.Count];
        for (int i = 0; i < members.Count; i++)
            converted[i] = new NexusMember(members[i].Role, members[i].Vertex.ToCore());
        return new NexusKey(WriteTransaction.CreateNexus(type, converted));
    }

    /// <summary>Vertex の vector property を設定する。</summary>
    public void SetVector(VertexKey vertex, string property, ReadOnlySpan<float> vector)
    {
        EnsureActive();
        ArgumentException.ThrowIfNullOrEmpty(property);
        WriteTransaction.SetVectorProperty(EntityRef.From(vertex.ToCore()), property, vector);
    }
}

/// <summary>同期読み取り callback の間だけ有効な scope。</summary>
public sealed class GraphReadScope : GraphReadAccess
{
    internal GraphReadScope(IReadTransaction transaction)
        : base(transaction)
    {
    }
}

/// <summary>同期書き込み callback の間だけ有効な scope。</summary>
public sealed class GraphWriteScope : GraphWriteAccess
{
    internal GraphWriteScope(IWriteTransaction transaction)
        : base(transaction)
    {
    }
}

/// <summary>長時間 snapshot または cursor 操作用の明示読み取り session。</summary>
public sealed class GraphReadSession : GraphReadAccess, IDisposable
{
    internal GraphReadSession(IReadTransaction transaction)
        : base(transaction)
    {
    }

    /// <summary>session とその snapshot を閉じる。</summary>
    public void Dispose()
    {
        Deactivate();
        Transaction.Dispose();
    }
}

/// <summary>明示 commit または rollback を必要とする書き込み session。</summary>
public sealed class GraphWriteSession : GraphWriteAccess, IDisposable
{
    private bool _completed;

    internal GraphWriteSession(IWriteTransaction transaction)
        : base(transaction)
    {
    }

    /// <summary>変更を commit して session を完了する。</summary>
    public void Commit()
    {
        EnsureActive();
        WriteTransaction.Commit();
        _completed = true;
        Deactivate();
    }

    /// <summary>変更を rollback して session を完了する。</summary>
    public void Rollback()
    {
        EnsureActive();
        WriteTransaction.Rollback();
        _completed = true;
        Deactivate();
    }

    /// <summary>未完了の変更を rollback して session を閉じる。</summary>
    public void Dispose()
    {
        if (!_completed)
            WriteTransaction.Rollback();
        _completed = true;
        Deactivate();
        WriteTransaction.Dispose();
    }
}
