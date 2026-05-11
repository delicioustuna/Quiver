using Quiver.Client;
using Quiver.Core;
using Quiver.Stores;
using System.Text;

namespace Quiver.Client.Match;

public sealed class MatchContext
{
    private readonly QueryRow _row;
    private readonly IGraphTransaction _tx;
    private readonly Dictionary<string, int> _varToColumn;

    internal MatchContext(QueryRow row, IGraphTransaction tx, Dictionary<string, int> varToColumn)
    {
        _row = row; _tx = tx; _varToColumn = varToColumn;
    }

    public MatchContextRow this[string variable]
    {
        get
        {
            var col = _varToColumn[variable];
            var nodeId = _row.GetNodeId(col);
            return new MatchContextRow(nodeId, _tx);
        }
    }

    public T Load<T>(string variable) where T : IGraphNode<T>
    {
        var col = _varToColumn[variable];
        return T.Load(_tx, _row.GetNodeId(col));
    }
}

public readonly struct MatchContextRow
{
    private readonly NodeId _nodeId;
    private readonly IGraphTransaction _tx;

    internal MatchContextRow(NodeId nodeId, IGraphTransaction tx)
    {
        _nodeId = nodeId; _tx = tx;
    }

    public T Get<T>(string key)
    {
        var value = _tx.GetProperty(_nodeId, key);
        if (typeof(T) == typeof(string))
            return (T)(object)Encoding.UTF8.GetString(value.Utf8StringValue);
        if (typeof(T) == typeof(long))
            return (T)(object)value.Int64Value;
        if (typeof(T) == typeof(int))
            return (T)(object)value.Int32Value;
        if (typeof(T) == typeof(double))
            return (T)(object)value.DoubleValue;
        if (typeof(T) == typeof(bool))
            return (T)(object)value.BoolValue;
        throw new NotSupportedException($"Type {typeof(T)} is not supported in MatchContextRow.Get<T>.");
    }
}
