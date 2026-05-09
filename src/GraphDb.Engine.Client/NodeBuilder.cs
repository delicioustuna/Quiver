using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine.Client;

public sealed class NodeBuilder
{
    private readonly IGraphTransaction _tx;
    private readonly string _label;
    private readonly List<Action<IGraphTransaction, NodeId>> _props = new();

    internal NodeBuilder(IGraphTransaction tx, string label) { _tx = tx; _label = label; }

    public NodeBuilder P(string key, string value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromString(value))); return this; }
    public NodeBuilder P(string key, int value)     { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt32(value)));  return this; }
    public NodeBuilder P(string key, long value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromInt64(value)));  return this; }
    public NodeBuilder P(string key, double value)  { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromDouble(value))); return this; }
    public NodeBuilder P(string key, bool value)    { _props.Add((tx, id) => tx.SetProperty(id, key, PropertyValue.FromBool(value)));   return this; }

    public NodeId Next()
    {
        var id = _tx.CreateNode(_label);
        foreach (var apply in _props) apply(_tx, id);
        return id;
    }
}
