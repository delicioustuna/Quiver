using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// <see cref="GraphTraversalSource.AddEdge"/> から開始するEdge追加ビルダ。
/// <see cref="From"/> と <see cref="To"/> で両端Vertexを指定し、必要なら <c>.P(...)</c> で
/// プロパティを追加し、最後に <see cref="Next"/> で実際にエッジを作成する。
/// </summary>
public sealed class EdgeBuilder
{
    private readonly IWriteTransaction _tx;
    private readonly string _type;
    private VertexId _from = VertexId.Invalid;
    private VertexId _to   = VertexId.Invalid;
    private readonly List<Action<IWriteTransaction, EdgeId>> _props = new();

    internal EdgeBuilder(IWriteTransaction tx, string type) { _tx = tx; _type = type; }

    /// <summary>始点Vertexを指定する。</summary>
    public EdgeBuilder From(VertexId src) { _from = src; return this; }

    /// <summary>終点Vertexを指定する。</summary>
    public EdgeBuilder To(VertexId dst)   { _to   = dst; return this; }

    /// <summary>文字列プロパティを追加する。</summary>
    public EdgeBuilder P(string key, string value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromString(value))); return this; }

    /// <summary><see cref="int"/> プロパティを追加する。</summary>
    public EdgeBuilder P(string key, int value)     { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt32(value)));  return this; }

    /// <summary><see cref="long"/> プロパティを追加する。</summary>
    public EdgeBuilder P(string key, long value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt64(value)));  return this; }

    /// <summary><see cref="double"/> プロパティを追加する。</summary>
    public EdgeBuilder P(string key, double value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromDouble(value))); return this; }

    /// <summary><see cref="bool"/> プロパティを追加する。</summary>
    public EdgeBuilder P(string key, bool value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromBool(value)));   return this; }

    /// <summary>蓄積されたプロパティを適用して実際にEdgeを作成し、その ID を返す。</summary>
    /// <exception cref="InvalidOperationException"><see cref="From"/> または <see cref="To"/> 未指定で呼び出した場合。</exception>
    public EdgeId Next()
    {
        if (!_from.IsValid) throw new InvalidOperationException("Next() の前に From() を呼び出してください。");
        if (!_to.IsValid)   throw new InvalidOperationException("Next() の前に To() を呼び出してください。");
        var id = _tx.CreateEdge(_from, _to, _type);
        foreach (var apply in _props) apply(_tx, id);
        return id;
    }
}
