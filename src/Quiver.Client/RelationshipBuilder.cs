using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Client;

/// <summary>
/// <see cref="GraphTraversalSource.AddRelationship"/> から開始するリレーションシップ追加ビルダ。
/// <see cref="From"/> と <see cref="To"/> で両端ノードを指定し、必要なら <c>.P(...)</c> で
/// プロパティを追加し、最後に <see cref="Next"/> で実際にエッジを作成する。
/// </summary>
public sealed class RelationshipBuilder
{
    private readonly IGraphTransaction _tx;
    private readonly string _type;
    private NodeId _from = NodeId.Invalid;
    private NodeId _to   = NodeId.Invalid;
    private readonly List<Action<IGraphTransaction, RelationshipId>> _props = new();

    internal RelationshipBuilder(IGraphTransaction tx, string type) { _tx = tx; _type = type; }

    /// <summary>始点ノードを指定する。</summary>
    public RelationshipBuilder From(NodeId src) { _from = src; return this; }

    /// <summary>終点ノードを指定する。</summary>
    public RelationshipBuilder To(NodeId dst)   { _to   = dst; return this; }

    /// <summary>文字列プロパティを追加する。</summary>
    public RelationshipBuilder P(string key, string value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromString(value))); return this; }

    /// <summary><see cref="int"/> プロパティを追加する。</summary>
    public RelationshipBuilder P(string key, int value)     { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt32(value)));  return this; }

    /// <summary><see cref="long"/> プロパティを追加する。</summary>
    public RelationshipBuilder P(string key, long value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt64(value)));  return this; }

    /// <summary><see cref="double"/> プロパティを追加する。</summary>
    public RelationshipBuilder P(string key, double value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromDouble(value))); return this; }

    /// <summary><see cref="bool"/> プロパティを追加する。</summary>
    public RelationshipBuilder P(string key, bool value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromBool(value)));   return this; }

    /// <summary>蓄積されたプロパティを適用して実際にリレーションシップを作成し、その ID を返す。</summary>
    /// <exception cref="InvalidOperationException"><see cref="From"/> または <see cref="To"/> 未指定で呼び出した場合。</exception>
    public RelationshipId Next()
    {
        if (!_from.IsValid) throw new InvalidOperationException("Next() の前に From() を呼び出してください。");
        if (!_to.IsValid)   throw new InvalidOperationException("Next() の前に To() を呼び出してください。");
        var id = _tx.CreateRelationship(_from, _to, _type);
        foreach (var apply in _props) apply(_tx, id);
        return id;
    }
}
