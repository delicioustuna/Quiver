using GraphDb.Engine.Client.Internal;
using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine.Client;

public sealed class GraphTraversal<T>
{
    internal readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;
    private readonly IOperatorBuilder _builder;
    private readonly Func<QueryRow, T> _projection;
    private readonly int _entityColumn;

    internal GraphTraversal(IGraphTransaction tx, ISchemaApi schema, IOperatorBuilder builder, Func<QueryRow, T> projection, int entityColumn)
    {
        _tx = tx; _schema = schema; _builder = builder; _projection = projection; _entityColumn = entityColumn;
    }

    // HasLabel: optimize when source is AllNodesScan, otherwise wrap in filter
    public GraphTraversal<NodeId> HasLabel(string label)
    {
        IOperatorBuilder next;
        if (_builder is ScanBuilder s && s.CurrentEntityColumn == 0)
            next = new ScanBuilder(label);
        else
        {
            var labelId = _schema.GetOrCreateLabel(label);
            var col = _entityColumn;
            next = new FilterBuilder(_builder, _ => new LabelPredicate(labelId, col));
        }
        return new GraphTraversal<NodeId>(_tx, _schema, next, row => row.GetNodeId(_entityColumn), next.CurrentEntityColumn);
    }

    public GraphTraversal<T> Has(string key, string value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col   = _entityColumn;
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, _ => new PropertyEqStringPredicate(col, keyId, value)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, int value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col   = _entityColumn;
        var pred  = P.Eq((long)value);
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, _ => new PropertyInt64Predicate(col, keyId, pred)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, long value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col   = _entityColumn;
        var pred  = P.Eq(value);
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, _ => new PropertyInt64Predicate(col, keyId, pred)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, double value)
    {
        var keyId   = _schema.GetOrCreatePropertyKey(key);
        var col     = _entityColumn;
        var encoded = BitConverter.DoubleToInt64Bits(value);
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, _ => new PropertyDoublePredicate(col, keyId, encoded)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, bool value)
    {
        var keyId  = _schema.GetOrCreatePropertyKey(key);
        var col    = _entityColumn;
        var scalar = value ? 1L : 0L;
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, _ => new PropertyBoolPredicate(col, keyId, scalar)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, PropertyPredicate pred)
    {
        var keyId  = _schema.GetOrCreatePropertyKey(key);
        var col    = _entityColumn;
        if (pred.Kind == PredicateKind.Eq && pred.StringValue != null)
        {
            var s = pred.StringValue;
            return new GraphTraversal<T>(_tx, _schema,
                new FilterBuilder(_builder, _ => new PropertyEqStringPredicate(col, keyId, s)),
                _projection, _entityColumn);
        }
        if (pred.Kind == PredicateKind.Within && pred.WithinValues != null)
        {
            var vals = pred.WithinValues;
            return new GraphTraversal<T>(_tx, _schema,
                new FilterBuilder(_builder, _ => new PropertyWithinStringPredicate(col, keyId, vals)),
                _projection, _entityColumn);
        }
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, _ => new PropertyInt64Predicate(col, keyId, pred)),
            _projection, _entityColumn);
    }

    public GraphTraversal<NodeId> Out(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Outgoing, type, ExpandOutputMode.NeighborOnly);
        return new GraphTraversal<NodeId>(_tx, _schema, expand, row => row.GetNodeId(expand.CurrentEntityColumn), expand.CurrentEntityColumn);
    }

    public GraphTraversal<NodeId> In(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Incoming, type, ExpandOutputMode.NeighborOnly);
        return new GraphTraversal<NodeId>(_tx, _schema, expand, row => row.GetNodeId(expand.CurrentEntityColumn), expand.CurrentEntityColumn);
    }

    public GraphTraversal<NodeId> Both(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Both, type, ExpandOutputMode.NeighborOnly);
        return new GraphTraversal<NodeId>(_tx, _schema, expand, row => row.GetNodeId(expand.CurrentEntityColumn), expand.CurrentEntityColumn);
    }

    public GraphTraversal<RelationshipId> OutE(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Outgoing, type, ExpandOutputMode.NeighborAndRel);
        return new GraphTraversal<RelationshipId>(_tx, _schema, expand, row => row.GetRelationshipId(0), 0);
    }

    public GraphTraversal<string> Values(string key)
    {
        var lookup = new PropertyLookupBuilder(_builder, key);
        int propCol = lookup.PredictedOutputColumnCount - 1;
        return new GraphTraversal<string>(_tx, _schema, lookup, row => row.GetString(propCol), _entityColumn);
    }

    public List<T> ToList()
    {
        var results = new List<T>();
        var result  = _tx.Execute(_builder.Build(_schema));
        foreach (var row in result.Rows())
            results.Add(_projection(row));
        return results;
    }

    public T Next()
    {
        var result = _tx.Execute(_builder.Build(_schema));
        foreach (var row in result.Rows())
            return _projection(row);
        throw new InvalidOperationException("Traversal produced no results.");
    }

    public T? TryNext()
    {
        var result = _tx.Execute(_builder.Build(_schema));
        foreach (var row in result.Rows())
            return _projection(row);
        return default;
    }

    public long Count()
    {
        long count = 0;
        var result = _tx.Execute(_builder.Build(_schema));
        foreach (var _ in result.Rows())
            count++;
        return count;
    }
}
