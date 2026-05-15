using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver.Client.Internal;

internal sealed class LabelPredicate : IPredicate
{
    private readonly int _column;
    private readonly LabelId _labelId;
    internal LabelPredicate(LabelId labelId, int column = 0) { _labelId = labelId; _column = column; }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_column].LongValue);
        using var h = tx.Nodes.Read(nodeId);
        return h.Label == _labelId;
    }
}

internal sealed class PropertyEqStringPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly string _value;

    internal PropertyEqStringPredicate(int nodeColumn, PropertyKeyId keyId, string value)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _value = value;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Properties.Enumerate(node.FirstPropertyId);
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.String) return false;
            return System.Text.Encoding.UTF8.GetString(prop.Value.Utf8StringValue) == _value;
        }
        return false;
    }
}

internal sealed class PropertyInt64Predicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly PropertyPredicate _pred;

    internal PropertyInt64Predicate(int nodeColumn, PropertyKeyId keyId, PropertyPredicate pred)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _pred = pred;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Properties.Enumerate(node.FirstPropertyId);
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            // BA-8: reject non-integer values without decoding.
            // (The previous fallback to Int64Value returned scrambled bits for
            // Double / Bool / String, so Gt(30) on a Double property would lie.)
            var flags = prop.Value.Type.ToFlags();
            if ((flags & (PropertyTypeFlags.Int32 | PropertyTypeFlags.Int64)) == PropertyTypeFlags.None)
                return false;
            long v = prop.Value.Type == PropertyValueType.Int32
                ? prop.Value.Int32Value
                : prop.Value.Int64Value;
            return _pred.Kind switch
            {
                PredicateKind.Eq      => v == _pred.LongFrom,
                PredicateKind.Gt      => v >  _pred.LongFrom,
                PredicateKind.Gte     => v >= _pred.LongFrom,
                PredicateKind.Lt      => v <  _pred.LongFrom,
                PredicateKind.Lte     => v <= _pred.LongFrom,
                PredicateKind.Between => v >= _pred.LongFrom && v < _pred.LongTo,
                _                     => false,
            };
        }
        return false;
    }
}

internal sealed class PropertyWithinStringPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly HashSet<string> _values;

    internal PropertyWithinStringPredicate(int nodeColumn, PropertyKeyId keyId, IEnumerable<string> values)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _values = new HashSet<string>(values);
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Properties.Enumerate(node.FirstPropertyId);
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.String) return false;
            return _values.Contains(System.Text.Encoding.UTF8.GetString(prop.Value.Utf8StringValue));
        }
        return false;
    }
}

internal sealed class PropertyDoublePredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly long _encodedValue;

    internal PropertyDoublePredicate(int nodeColumn, PropertyKeyId keyId, long encodedValue)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _encodedValue = encodedValue;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Properties.Enumerate(node.FirstPropertyId);
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.Double) return false;
            return BitConverter.DoubleToInt64Bits(prop.Value.DoubleValue) == _encodedValue;
        }
        return false;
    }
}

/// <summary>
/// GC-1: existence test for a property key on a node. Used by
/// <c>.Has(key)</c> (mustExist=true) and <c>.HasNot(key)</c> (mustExist=false).
/// The mustExist flag inlines negation so GC-1 does not need to wait on
/// GC-2's NegatedPredicate.
/// </summary>
internal sealed class PropertyExistsPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly bool _mustExist;

    internal PropertyExistsPredicate(int nodeColumn, PropertyKeyId keyId, bool mustExist)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _mustExist = mustExist;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        // Unknown property key (token store never observed it) ⇒ definitely
        // absent. HasNot(key) returns true, Has(key) returns false.
        if (!_keyId.IsValid) return !_mustExist;

        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Properties.Enumerate(node.FirstPropertyId);
        while (en.MoveNext())
        {
            if (en.Current.KeyId == _keyId) return _mustExist;
        }
        return !_mustExist;
    }
}

/// <summary>GC-1: <c>P.Without(...)</c> — string property must not match any listed value.</summary>
internal sealed class PropertyWithoutStringPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly HashSet<string> _values;

    internal PropertyWithoutStringPredicate(int nodeColumn, PropertyKeyId keyId, IEnumerable<string> values)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _values = new HashSet<string>(values);
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Properties.Enumerate(node.FirstPropertyId);
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.String) return true;
            return !_values.Contains(System.Text.Encoding.UTF8.GetString(prop.Value.Utf8StringValue));
        }
        // Missing property: caller's choice; we follow the Gremlin convention
        // that "without X" includes elements that don't have the key at all.
        return true;
    }
}

internal sealed class PropertyBoolPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly long _scalar;

    internal PropertyBoolPredicate(int nodeColumn, PropertyKeyId keyId, long scalar)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _scalar = scalar;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Properties.Enumerate(node.FirstPropertyId);
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.Bool) return false;
            return (prop.Value.BoolValue ? 1L : 0L) == _scalar;
        }
        return false;
    }
}
