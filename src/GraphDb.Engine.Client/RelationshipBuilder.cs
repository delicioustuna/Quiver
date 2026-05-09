using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine.Client;

public sealed class RelationshipBuilder
{
    private readonly IGraphTransaction _tx;
    private readonly string _type;
    private NodeId _from = NodeId.Invalid;
    private NodeId _to   = NodeId.Invalid;
    private readonly List<Action<IGraphTransaction, RelationshipId>> _props = new();

    internal RelationshipBuilder(IGraphTransaction tx, string type) { _tx = tx; _type = type; }

    public RelationshipBuilder From(NodeId src) { _from = src; return this; }
    public RelationshipBuilder To(NodeId dst)   { _to   = dst; return this; }

    public RelationshipBuilder P(string key, string value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromString(value))); return this; }
    public RelationshipBuilder P(string key, int value)     { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt32(value)));  return this; }
    public RelationshipBuilder P(string key, long value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt64(value)));  return this; }
    public RelationshipBuilder P(string key, double value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromDouble(value))); return this; }
    public RelationshipBuilder P(string key, bool value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromBool(value)));   return this; }

    public RelationshipId Next()
    {
        if (!_from.IsValid) throw new InvalidOperationException("From() must be called before Next().");
        if (!_to.IsValid)   throw new InvalidOperationException("To() must be called before Next().");
        var id = _tx.CreateRelationship(_from, _to, _type);
        foreach (var apply in _props) apply(_tx, id);
        return id;
    }
}
