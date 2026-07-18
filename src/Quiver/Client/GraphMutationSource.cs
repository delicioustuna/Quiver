using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// 書き込みトランザクションに所有される graph mutation の入口。
/// query を組み立てる場合は同じトランザクションの <see cref="IReadTransaction.Query"/> を使う。
/// </summary>
public sealed class GraphMutationSource
{
    private readonly IWriteTransaction _transaction;

    internal GraphMutationSource(IWriteTransaction transaction)
        => _transaction = transaction;

    internal IWriteTransaction Transaction => _transaction;

    /// <summary>新しいVertexの作成を開始する。</summary>
    public VertexBuilder AddVertex(string label) => new(_transaction, label);

    /// <summary>新しいEdgeの作成を開始する。</summary>
    public EdgeBuilder AddEdge(string type) => new(_transaction, type);

    /// <summary>新しいNexusの作成を開始する。</summary>
    public NexusBuilder AddNexus(string type) => new(_transaction, type);

    /// <summary>ラベルと scalar property が一致するVertexを取得し、無ければ作成する。</summary>
    public (VertexId Id, bool Created) MergeVertex(
        string label,
        string matchKey,
        in PropertyValue matchValue)
        => _transaction.MergeVertex(label, matchKey, in matchValue);

    /// <summary>source、type、target が一致するEdgeを取得し、無ければ作成する。</summary>
    public (EdgeId Id, bool Created) MergeEdge(
        VertexId source,
        VertexId target,
        string type)
        => _transaction.MergeEdge(source, target, type);

    /// <summary>型と role 付き member 集合をキーに Nexus を冪等作成する。</summary>
    public (NexusId Id, bool Created) MergeNexus(
        string type,
        ReadOnlySpan<NexusMember> members)
        => _transaction.MergeNexus(type, members);

    /// <summary>型付きVertexを追加する。</summary>
    public VertexId Insert<T>(T entity) where T : IGraphVertex<T>
        => T.Insert(_transaction, entity);

    /// <summary>型付きVertexを追加し、definition-driven index maintenance を適用する。</summary>
    public VertexId InsertIndexed<T>(T entity) where T : IGraphVertex<T>
        => T.InsertIndexed(_transaction, entity);

    /// <summary>型付きVertexのプロパティを更新する。</summary>
    public void Update<T>(VertexId id, T entity) where T : IGraphVertex<T>
        => T.Update(_transaction, id, entity);

    /// <summary>型付きVertexを削除する。</summary>
    public void Delete<T>(VertexId id) where T : IGraphVertex<T>
        => T.Delete(_transaction, id);
}
