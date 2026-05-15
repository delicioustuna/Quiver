using Quiver.Client.Internal;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;

namespace Quiver.Client;

public sealed class GraphTraversal<T>
{
    internal readonly IGraphTransaction _tx;
    internal readonly ISchemaApi _schema;
    internal readonly IOperatorBuilder _builder;
    internal readonly Func<QueryRow, T> _projection;
    internal readonly int _entityColumn;

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
        if (pred.Kind == PredicateKind.Without && pred.WithinValues != null)
        {
            var vals = pred.WithinValues;
            return new GraphTraversal<T>(_tx, _schema,
                new FilterBuilder(_builder, _ => new PropertyWithoutStringPredicate(col, keyId, vals)),
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

    public GraphTraversal<NodeId> Out<TRel>() where TRel : IGraphRelationship<TRel>
        => Out(TRel.GraphType);

    public GraphTraversal<NodeId> In(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Incoming, type, ExpandOutputMode.NeighborOnly);
        return new GraphTraversal<NodeId>(_tx, _schema, expand, row => row.GetNodeId(expand.CurrentEntityColumn), expand.CurrentEntityColumn);
    }

    public GraphTraversal<NodeId> In<TRel>() where TRel : IGraphRelationship<TRel>
        => In(TRel.GraphType);

    public GraphTraversal<NodeId> Both(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Both, type, ExpandOutputMode.NeighborOnly);
        return new GraphTraversal<NodeId>(_tx, _schema, expand, row => row.GetNodeId(expand.CurrentEntityColumn), expand.CurrentEntityColumn);
    }

    public GraphTraversal<NodeId> Both<TRel>() where TRel : IGraphRelationship<TRel>
        => Both(TRel.GraphType);

    public GraphTraversal<RelationshipId> OutE(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Outgoing, type, ExpandOutputMode.NeighborAndRel);
        return new GraphTraversal<RelationshipId>(_tx, _schema, expand, row => row.GetRelationshipId(0), 0);
    }

    public GraphTraversal<RelationshipId> OutE<TRel>() where TRel : IGraphRelationship<TRel>
        => OutE(TRel.GraphType);

    public GraphTraversal<RelationshipId> InE(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Incoming, type, ExpandOutputMode.NeighborAndRel);
        return new GraphTraversal<RelationshipId>(_tx, _schema, expand, row => row.GetRelationshipId(0), 0);
    }

    public GraphTraversal<RelationshipId> InE<TRel>() where TRel : IGraphRelationship<TRel>
        => InE(TRel.GraphType);

    public GraphTraversal<RelationshipId> BothE(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Both, type, ExpandOutputMode.NeighborAndRel);
        return new GraphTraversal<RelationshipId>(_tx, _schema, expand, row => row.GetRelationshipId(0), 0);
    }

    public GraphTraversal<RelationshipId> BothE<TRel>() where TRel : IGraphRelationship<TRel>
        => BothE(TRel.GraphType);

    /// <summary>
    /// WHERE EXISTS サブトラバーサルでフィルタする。
    /// 例: .Where(t => t.Out("KNOWS")) — KNOWS エッジを持つノードのみを通す。
    /// </summary>
    public GraphTraversal<T> Where(Func<SubTraversal, SubTraversal> innerTraversal)
    {
        var schema = _schema;
        var outerEntityColumn = _entityColumn;
        var capturedInner = innerTraversal;
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, s =>
            {
                var probe = new CorrelatedInputOperator();
                var seed = new CorrelatedSeedBuilder(probe);
                var start = new SubTraversal(probe, seed, s, 0);
                return capturedInner(start).BuildExistsPredicate(outerEntityColumn);
            }),
            _projection, _entityColumn);
    }

    /// <summary>
    /// WHERE NOT EXISTS サブトラバーサルでフィルタする。
    /// 例: .Not(t => t.Out("KNOWS")) — KNOWS エッジを持たないノードのみを通す。
    /// </summary>
    public GraphTraversal<T> Not(Func<SubTraversal, SubTraversal> innerTraversal)
    {
        var schema = _schema;
        var outerEntityColumn = _entityColumn;
        var capturedInner = innerTraversal;
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, s =>
            {
                var probe = new CorrelatedInputOperator();
                var seed = new CorrelatedSeedBuilder(probe);
                var start = new SubTraversal(probe, seed, s, 0);
                return capturedInner(start).BuildNotExistsPredicate(outerEntityColumn);
            }),
            _projection, _entityColumn);
    }

    // ── GC-1: presence checks ────────────────────────────────────────────────

    /// <summary>GC-1: keep elements that carry property <paramref name="key"/>.</summary>
    public GraphTraversal<T> Has(string key)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col = _entityColumn;
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, _ => new PropertyExistsPredicate(col, keyId, mustExist: true)),
            _projection, _entityColumn);
    }

    /// <summary>GC-1: keep elements that do NOT carry property <paramref name="key"/> (Gremlin <c>.hasNot</c>).</summary>
    public GraphTraversal<T> HasNot(string key)
    {
        // Use TryGet so we don't burn a fresh token id just to filter against it.
        // We can't call ISchemaApi.TryGet here (no such method exposed), so the
        // worst case is a single token allocation on first call — still correct
        // because newly-created keys have zero observations.
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col = _entityColumn;
        return new GraphTraversal<T>(_tx, _schema,
            new FilterBuilder(_builder, _ => new PropertyExistsPredicate(col, keyId, mustExist: false)),
            _projection, _entityColumn);
    }

    // ── GC-1: pagination ─────────────────────────────────────────────────────

    /// <summary>GC-1: emit at most <paramref name="n"/> elements (Gremlin <c>.limit</c>).</summary>
    public GraphTraversal<T> Limit(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        return new GraphTraversal<T>(_tx, _schema, new LimitBuilder(_builder, n, skip: 0), _projection, _entityColumn);
    }

    /// <summary>GC-1: discard the first <paramref name="n"/> elements before emitting (Gremlin <c>.skip</c>).</summary>
    public GraphTraversal<T> Skip(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        return new GraphTraversal<T>(_tx, _schema, new LimitBuilder(_builder, long.MaxValue, skip: n), _projection, _entityColumn);
    }

    /// <summary>GC-1: emit the half-open <c>[from, to)</c> window (Gremlin <c>.range(a, b)</c>).</summary>
    public GraphTraversal<T> Range(long from, long to)
    {
        if (from < 0 || to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "Require 0 <= from <= to.");
        return new GraphTraversal<T>(_tx, _schema, new LimitBuilder(_builder, to - from, skip: from), _projection, _entityColumn);
    }

    // ── GC-1: terminal / existence ──────────────────────────────────────────

    /// <summary>GC-1: <c>true</c> when the traversal would emit at least one element (Gremlin <c>.hasNext</c>).</summary>
    public bool HasNext()
    {
        // TryNext() returns default(T) when the stream is empty, which for
        // value-type projections (NodeId, long) is indistinguishable from a
        // legitimate zero-value result. Drive a cursor directly so we can
        // rely on MoveNext()'s boolean.
        using var cursor = AsCursor();
        return cursor.MoveNext();
    }

    // ── GC-1: <c>.label()</c> step ──────────────────────────────────────────

    /// <summary>
    /// GC-1: Gremlin <c>.label()</c> — for each node in the current traversal,
    /// resolve its label name through the schema. Only valid when the current
    /// entity column carries a NodeId; chaining after an edge traversal will
    /// resolve garbage.
    /// </summary>
    public GraphTraversal<string> Label()
    {
        var lookup = new LabelNameLookupBuilder(_builder, _entityColumn, _schema);
        int labelCol = lookup.PredictedOutputColumnCount - 1;
        return new GraphTraversal<string>(_tx, _schema, lookup, row => row.GetString(labelCol), _entityColumn);
    }

    // ── GC-1: <c>.id()</c> step ─────────────────────────────────────────────

    /// <summary>
    /// GC-1: Gremlin <c>.id()</c> — project the current entity id as a raw
    /// <c>long</c>. Works for NodeId, RelationshipId, and PropertyId columns
    /// alike since all three are stored as <see cref="TupleSlot.LongValue"/>.
    /// </summary>
    public GraphTraversal<long> Id()
    {
        var col = _entityColumn;
        return new GraphTraversal<long>(_tx, _schema, _builder, row => row.GetInt64(col), _entityColumn);
    }

    // ── GC-1: edge endpoint resolution ──────────────────────────────────────

    /// <summary>GC-1: Gremlin <c>.outV()</c> — resolve to the source node of the current edge.</summary>
    public GraphTraversal<NodeId> OutV()
    {
        var rep = new RelationshipEndpointBuilder(_builder, _entityColumn, RelationshipEndpoint.Source);
        return new GraphTraversal<NodeId>(_tx, _schema, rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>GC-1: Gremlin <c>.inV()</c> — resolve to the target node of the current edge.</summary>
    public GraphTraversal<NodeId> InV()
    {
        var rep = new RelationshipEndpointBuilder(_builder, _entityColumn, RelationshipEndpoint.Target);
        return new GraphTraversal<NodeId>(_tx, _schema, rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>
    /// GC-1: Gremlin <c>.otherV()</c> — resolve to the "far" endpoint
    /// relative to the entry direction. Without a context node the operator
    /// defaults to target; use OutV / InV when the side matters precisely.
    /// </summary>
    public GraphTraversal<NodeId> OtherV()
    {
        var rep = new RelationshipEndpointBuilder(_builder, _entityColumn, RelationshipEndpoint.Other);
        return new GraphTraversal<NodeId>(_tx, _schema, rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>
    /// VEC-6: graph-first KNN. Drains the current traversal as a NodeId
    /// candidate set, then keeps only the top-<paramref name="k"/> by vector
    /// similarity against <paramref name="query"/>. Pairs with
    /// <c>g.Knn(...)</c> (vector-first) — use <c>QueryOptimizer.ChooseKnnStrategy</c>
    /// to decide which to invoke when both are viable.
    /// </summary>
    /// <remarks>
    /// Only valid when the traversal currently produces <see cref="NodeId"/>s
    /// (i.e. <typeparamref name="T"/> is NodeId). The resulting traversal
    /// emits NodeId in descending similarity order; score is not surfaced.
    /// </remarks>
    public GraphTraversal<NodeId> FilterByKnn(string indexName, ReadOnlySpan<float> query, int k)
    {
        var filtered = new Internal.FilteredKnnNodeSourceBuilder(_builder, indexName, query, k);
        return new GraphTraversal<NodeId>(_tx, _schema, filtered, row => row.GetNodeId(0), 0);
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

    /// <summary>
    /// Returns a streaming cursor over the results. The caller owns the cursor lifetime
    /// and must dispose it. The cursor is valid only within the owning transaction.
    /// </summary>
    public ITraversalCursor<T> AsCursor()
    {
        var cursor = _tx.ExecuteCursor(_builder.Build(_schema));
        return new TraversalCursor<T>(cursor, _projection);
    }

    /// <summary>
    /// Streams results one-by-one without materializing the full list.
    /// Valid only within the owning transaction.
    /// </summary>
    public IEnumerable<T> AsEnumerable()
    {
        using var cursor = _tx.ExecuteCursor(_builder.Build(_schema));
        while (cursor.MoveNext())
            yield return _projection(cursor.Current);
    }
}
