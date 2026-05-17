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
    // GC-6: name → column index. Null when no alias has ever been bound on
    // this chain (the common case). Treated as immutable — replaced wholesale,
    // never mutated in place.
    internal readonly Dictionary<string, int>? _aliases;

    internal GraphTraversal(
        IGraphTransaction tx,
        ISchemaApi schema,
        IOperatorBuilder builder,
        Func<QueryRow, T> projection,
        int entityColumn,
        Dictionary<string, int>? aliases = null)
    {
        _tx = tx; _schema = schema; _builder = builder; _projection = projection;
        _entityColumn = entityColumn;
        _aliases = (aliases is { Count: > 0 }) ? aliases : null;
    }

    /// <summary>GC-6: build a chained traversal with the same alias set.</summary>
    private GraphTraversal<U> Chain<U>(IOperatorBuilder builder, Func<QueryRow, U> projection, int entityColumn)
        => new(_tx, _schema, builder, projection, entityColumn, _aliases);

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
        return new GraphTraversal<NodeId>(_tx, _schema, next, row => row.GetNodeId(_entityColumn), next.CurrentEntityColumn, _aliases);
    }

    public GraphTraversal<T> Has(string key, string value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col   = _entityColumn;
        return Chain(
            new FilterBuilder(_builder, _ => new PropertyEqStringPredicate(col, keyId, value)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, int value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col   = _entityColumn;
        var pred  = P.Eq((long)value);
        return Chain(
            new FilterBuilder(_builder, _ => new PropertyInt64Predicate(col, keyId, pred)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, long value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col   = _entityColumn;
        var pred  = P.Eq(value);
        return Chain(
            new FilterBuilder(_builder, _ => new PropertyInt64Predicate(col, keyId, pred)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, double value)
    {
        var keyId   = _schema.GetOrCreatePropertyKey(key);
        var col     = _entityColumn;
        var encoded = BitConverter.DoubleToInt64Bits(value);
        return Chain(
            new FilterBuilder(_builder, _ => new PropertyDoublePredicate(col, keyId, encoded)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, bool value)
    {
        var keyId  = _schema.GetOrCreatePropertyKey(key);
        var col    = _entityColumn;
        var scalar = value ? 1L : 0L;
        return Chain(
            new FilterBuilder(_builder, _ => new PropertyBoolPredicate(col, keyId, scalar)),
            _projection, _entityColumn);
    }

    public GraphTraversal<T> Has(string key, PropertyPredicate pred)
    {
        var keyId  = _schema.GetOrCreatePropertyKey(key);
        var col    = _entityColumn;
        return Chain(
            new FilterBuilder(_builder, _ => PredicateDispatch.Build(col, keyId, pred)),
            _projection, _entityColumn);
    }

    public GraphTraversal<NodeId> Out(string? type = null) => Expand(Direction.Outgoing, type);
    public GraphTraversal<NodeId> Out<TRel>() where TRel : IGraphRelationship<TRel> => Out(TRel.GraphType);

    public GraphTraversal<NodeId> In(string? type = null) => Expand(Direction.Incoming, type);
    public GraphTraversal<NodeId> In<TRel>() where TRel : IGraphRelationship<TRel> => In(TRel.GraphType);

    public GraphTraversal<NodeId> Both(string? type = null) => Expand(Direction.Both, type);
    public GraphTraversal<NodeId> Both<TRel>() where TRel : IGraphRelationship<TRel> => Both(TRel.GraphType);

    private GraphTraversal<NodeId> Expand(Direction direction, string? type)
    {
        // GC-6: when aliases are live, copy them through the expansion so
        // .Select(alias) downstream still finds the original entity. Without
        // aliases this is identical to the pre-GC-6 fast path (no extra cols).
        // Pass _entityColumn explicitly so .Select(alias).Out(...) expands
        // from the pinned column, not from the most recent builder's output.
        if (_aliases is null)
        {
            var fast = new ExpandBuilder(_builder, direction, type, ExpandOutputMode.NeighborOnly, sourceColumnOverride: _entityColumn);
            return new GraphTraversal<NodeId>(_tx, _schema, fast, row => row.GetNodeId(fast.CurrentEntityColumn), fast.CurrentEntityColumn);
        }

        var (carry, newAliases) = RemapForExpand(baseColumnCount: 1);
        var expand = new ExpandBuilder(_builder, direction, type, ExpandOutputMode.NeighborOnly, carry, sourceColumnOverride: _entityColumn);
        return new GraphTraversal<NodeId>(_tx, _schema, expand, row => row.GetNodeId(0), 0, newAliases);
    }

    public GraphTraversal<RelationshipId> OutE(string? type = null) => ExpandE(Direction.Outgoing, type);
    public GraphTraversal<RelationshipId> OutE<TRel>() where TRel : IGraphRelationship<TRel> => OutE(TRel.GraphType);

    public GraphTraversal<RelationshipId> InE(string? type = null) => ExpandE(Direction.Incoming, type);
    public GraphTraversal<RelationshipId> InE<TRel>() where TRel : IGraphRelationship<TRel> => InE(TRel.GraphType);

    public GraphTraversal<RelationshipId> BothE(string? type = null) => ExpandE(Direction.Both, type);
    public GraphTraversal<RelationshipId> BothE<TRel>() where TRel : IGraphRelationship<TRel> => BothE(TRel.GraphType);

    private GraphTraversal<RelationshipId> ExpandE(Direction direction, string? type)
    {
        if (_aliases is null)
        {
            var fast = new ExpandBuilder(_builder, direction, type, ExpandOutputMode.NeighborAndRel, sourceColumnOverride: _entityColumn);
            return new GraphTraversal<RelationshipId>(_tx, _schema, fast, row => row.GetRelationshipId(0), 0);
        }

        // NeighborAndRel emits 2 cols (rel@0, neighbor@1). The "current" column
        // for chained .OutV()/.InV() stays at 0 (rel), so carries start at 2.
        var (carry, newAliases) = RemapForExpand(baseColumnCount: 2);
        var e = new ExpandBuilder(_builder, direction, type, ExpandOutputMode.NeighborAndRel, carry, sourceColumnOverride: _entityColumn);
        return new GraphTraversal<RelationshipId>(_tx, _schema, e, row => row.GetRelationshipId(0), 0, newAliases);
    }

    /// <summary>
    /// GC-6: shared helper for Out/In/Both/OutE/InE/BothE. Returns the
    /// sorted-distinct upstream column list to carry plus the rewritten alias
    /// map pointing at the new tail positions.
    /// </summary>
    private (int[] carry, Dictionary<string, int> newAliases) RemapForExpand(int baseColumnCount)
    {
        // Distinct + sorted so the alias→new-column mapping is deterministic.
        var carry = new SortedSet<int>(_aliases!.Values).ToArray();
        var newAliases = new Dictionary<string, int>(_aliases.Count);
        foreach (var (label, oldCol) in _aliases)
        {
            int idx = Array.IndexOf(carry, oldCol);
            newAliases[label] = baseColumnCount + idx;
        }
        return (carry, newAliases);
    }

    /// <summary>
    /// WHERE EXISTS サブトラバーサルでフィルタする。
    /// 例: .Where(t => t.Out("KNOWS")) — KNOWS エッジを持つノードのみを通す。
    /// </summary>
    public GraphTraversal<T> Where(Func<SubTraversal, SubTraversal> innerTraversal)
    {
        var schema = _schema;
        var outerEntityColumn = _entityColumn;
        var capturedInner = innerTraversal;
        return Chain(
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
        return Chain(
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
        return Chain(
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
        return Chain(
            new FilterBuilder(_builder, _ => new PropertyExistsPredicate(col, keyId, mustExist: false)),
            _projection, _entityColumn);
    }

    /// <summary>
    /// GC-2: Cypher <c>IS NULL</c> — keep elements where <paramref name="key"/>
    /// is absent. Sugar for <see cref="HasNot(string)"/>.
    /// </summary>
    public GraphTraversal<T> IsNull(string key) => HasNot(key);

    /// <summary>
    /// GC-2: Cypher <c>IS NOT NULL</c> — keep elements where <paramref name="key"/>
    /// is present. Sugar for <see cref="Has(string)"/>.
    /// </summary>
    public GraphTraversal<T> IsNotNull(string key) => Has(key);

    // ── GC-2: traversal-level boolean composition ────────────────────────────

    /// <summary>
    /// GC-2: Gremlin <c>.and(t1, t2, …)</c> — keep elements for which every
    /// sub-traversal produces at least one row.
    /// </summary>
    public GraphTraversal<T> And(params Func<SubTraversal, SubTraversal>[] traversals)
    {
        if (traversals is null || traversals.Length == 0)
            throw new ArgumentException("And requires at least one sub-traversal.", nameof(traversals));
        return CombineSubTraversals(traversals, useOr: false);
    }

    /// <summary>GC-2: Gremlin <c>.or(t1, t2, …)</c> — keep elements where any sub-traversal matches.</summary>
    public GraphTraversal<T> Or(params Func<SubTraversal, SubTraversal>[] traversals)
    {
        if (traversals is null || traversals.Length == 0)
            throw new ArgumentException("Or requires at least one sub-traversal.", nameof(traversals));
        return CombineSubTraversals(traversals, useOr: true);
    }

    private GraphTraversal<T> CombineSubTraversals(Func<SubTraversal, SubTraversal>[] traversals, bool useOr)
    {
        var outerEntityColumn = _entityColumn;
        var captured = traversals;
        return Chain(
            new FilterBuilder(_builder, s =>
            {
                var inners = new IPredicate[captured.Length];
                for (int i = 0; i < captured.Length; i++)
                {
                    var probe = new CorrelatedInputOperator();
                    var seed = new CorrelatedSeedBuilder(probe);
                    var start = new SubTraversal(probe, seed, s, 0);
                    inners[i] = captured[i](start).BuildExistsPredicate(outerEntityColumn);
                }
                return useOr ? new OrPredicate(inners) : new AndPredicate(inners);
            }),
            _projection, _entityColumn);
    }

    // ── GC-1: pagination ─────────────────────────────────────────────────────

    /// <summary>GC-1: emit at most <paramref name="n"/> elements (Gremlin <c>.limit</c>).</summary>
    public GraphTraversal<T> Limit(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        return Chain(new LimitBuilder(_builder, n, skip: 0), _projection, _entityColumn);
    }

    /// <summary>GC-1: discard the first <paramref name="n"/> elements before emitting (Gremlin <c>.skip</c>).</summary>
    public GraphTraversal<T> Skip(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        return Chain(new LimitBuilder(_builder, long.MaxValue, skip: n), _projection, _entityColumn);
    }

    /// <summary>GC-1: emit the half-open <c>[from, to)</c> window (Gremlin <c>.range(a, b)</c>).</summary>
    public GraphTraversal<T> Range(long from, long to)
    {
        if (from < 0 || to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "Require 0 <= from <= to.");
        return Chain(new LimitBuilder(_builder, to - from, skip: from), _projection, _entityColumn);
    }

    // ── GC-1: terminal / existence ──────────────────────────────────────────

    /// <summary>GC-1: <c>true</c> when the traversal would emit at least one element (Gremlin <c>.hasNext</c>).</summary>
    public bool HasNext()
    {
        using var cursor = AsCursor();
        return cursor.MoveNext();
    }

    // ── GC-1: <c>.label()</c> step ──────────────────────────────────────────

    public GraphTraversal<string> Label()
    {
        var lookup = new LabelNameLookupBuilder(_builder, _entityColumn, _schema);
        int labelCol = lookup.PredictedOutputColumnCount - 1;
        return Chain(lookup, row => row.GetString(labelCol), _entityColumn);
    }

    // ── GC-1: <c>.id()</c> step ─────────────────────────────────────────────

    public GraphTraversal<long> Id()
    {
        var col = _entityColumn;
        return Chain(_builder, row => row.GetInt64(col), _entityColumn);
    }

    // ── GC-1: edge endpoint resolution ──────────────────────────────────────

    /// <summary>GC-1: Gremlin <c>.outV()</c> — resolve to the source node of the current edge.</summary>
    public GraphTraversal<NodeId> OutV()
    {
        // RelationshipEndpointOperator emits a single-NodeId tuple, discarding
        // upstream — so any live aliases would be lost. Drop them silently;
        // documented as a GC-6 Phase-2 limitation.
        var rep = new RelationshipEndpointBuilder(_builder, _entityColumn, RelationshipEndpoint.Source);
        return new GraphTraversal<NodeId>(_tx, _schema, rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>GC-1: Gremlin <c>.inV()</c> — resolve to the target node of the current edge.</summary>
    public GraphTraversal<NodeId> InV()
    {
        var rep = new RelationshipEndpointBuilder(_builder, _entityColumn, RelationshipEndpoint.Target);
        return new GraphTraversal<NodeId>(_tx, _schema, rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>GC-1: Gremlin <c>.otherV()</c> — resolve to the "far" endpoint relative to the entry direction.</summary>
    public GraphTraversal<NodeId> OtherV()
    {
        var rep = new RelationshipEndpointBuilder(_builder, _entityColumn, RelationshipEndpoint.Other);
        return new GraphTraversal<NodeId>(_tx, _schema, rep, row => row.GetNodeId(0), 0);
    }

    /// <summary>VEC-6: graph-first KNN. See class docs for full semantics.</summary>
    public GraphTraversal<NodeId> FilterByKnn(string indexName, ReadOnlySpan<float> query, int k)
    {
        var filtered = new Internal.FilteredKnnNodeSourceBuilder(_builder, indexName, query, k);
        return new GraphTraversal<NodeId>(_tx, _schema, filtered, row => row.GetNodeId(0), 0);
    }

    // ── GC-3: ordering ───────────────────────────────────────────────────────

    public GraphTraversal<T> OrderBy(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Chain(new SortBuilder(_builder, key, descending: false), _projection, _entityColumn);
    }

    public GraphTraversal<T> OrderByDescending(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Chain(new SortBuilder(_builder, key, descending: true), _projection, _entityColumn);
    }

    public GraphTraversal<T> Order(bool descending = false)
        => Chain(new SortBuilder(_builder, _entityColumn, descending), _projection, _entityColumn);

    // ── GC-3: numeric aggregation (terminal, takes a property key) ───────────

    public double Sum(string key) => AggregateNumeric(key, AggregateKind.Sum) ?? 0.0;
    public long SumLong(string key) => (long)(AggregateLongSum(key) ?? 0L);
    public double? Max(string key) => AggregateNumeric(key, AggregateKind.Max);
    public double? Min(string key) => AggregateNumeric(key, AggregateKind.Min);
    public double? Mean(string key)
    {
        double sum = 0; long count = 0;
        ForEachNumeric(key, v => { sum += v; count++; });
        return count == 0 ? null : sum / count;
    }

    private enum AggregateKind { Sum, Max, Min }

    private double? AggregateNumeric(string key, AggregateKind kind)
    {
        double acc = 0; bool seen = false;
        ForEachNumeric(key, v =>
        {
            if (!seen) { acc = v; seen = true; return; }
            acc = kind switch
            {
                AggregateKind.Sum => acc + v,
                AggregateKind.Max => v > acc ? v : acc,
                AggregateKind.Min => v < acc ? v : acc,
                _ => acc,
            };
        });
        return seen ? acc : null;
    }

    private long? AggregateLongSum(string key)
    {
        long acc = 0; bool seen = false;
        var plan = new PropertyLookupBuilder(_builder, key).Build(_schema);
        using var cursor = _tx.ExecuteCursor(plan);
        int valueCol = cursor.Schema.Columns.Count - 1;
        while (cursor.MoveNext())
        {
            var row = cursor.Current;
            if (row.GetSlotType(valueCol) != TupleSlotType.Int64) continue;
            acc += row.GetInt64(valueCol);
            seen = true;
        }
        return seen ? acc : null;
    }

    private void ForEachNumeric(string key, Action<double> sink)
    {
        var plan = new PropertyLookupBuilder(_builder, key).Build(_schema);
        using var cursor = _tx.ExecuteCursor(plan);
        int valueCol = cursor.Schema.Columns.Count - 1;
        while (cursor.MoveNext())
        {
            var row = cursor.Current;
            switch (row.GetSlotType(valueCol))
            {
                case TupleSlotType.Int64:  sink(row.GetInt64(valueCol)); break;
                case TupleSlotType.Double: sink(row.GetDouble(valueCol)); break;
            }
        }
    }

    // ── GC-3: grouping ───────────────────────────────────────────────────────

    public Dictionary<string, long> GroupCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dict = new Dictionary<string, long>(StringComparer.Ordinal);
        var plan = new PropertyLookupBuilder(_builder, key).Build(_schema);
        using var cursor = _tx.ExecuteCursor(plan);
        int valueCol = cursor.Schema.Columns.Count - 1;
        while (cursor.MoveNext())
        {
            var row = cursor.Current;
            if (row.GetSlotType(valueCol) != TupleSlotType.Utf8String) continue;
            var s = row.GetString(valueCol);
            dict[s] = dict.GetValueOrDefault(s) + 1;
        }
        return dict;
    }

    // ── GC-3: fold ───────────────────────────────────────────────────────────

    public List<T> Fold() => ToList();

    // ── GC-4: dedup ──────────────────────────────────────────────────────────

    public GraphTraversal<T> Dedup() => Chain(new DedupBuilder(_builder, _entityColumn), _projection, _entityColumn);

    // ── GC-4: variable-length repeat ─────────────────────────────────────────

    public GraphTraversal<NodeId> Repeat(Action<RepeatStep> step, int times, bool emit = false)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (times < 1) throw new ArgumentOutOfRangeException(nameof(times), "Repeat requires times >= 1.");
        var rs = new RepeatStep();
        step(rs);
        int minHops = emit ? 1 : times;
        var b = new VarLenExpandBuilder(_builder, rs.Direction, rs.TypeFilter, minHops, times);
        int endCol = b.CurrentEntityColumn;
        return new GraphTraversal<NodeId>(_tx, _schema, b, row => row.GetNodeId(endCol), endCol);
    }

    // ── GC-4: shortest path ──────────────────────────────────────────────────

    public GraphTraversal<long> ShortestPathTo(
        NodeId target,
        Direction direction = Direction.Outgoing,
        string? type = null,
        long maxDistance = long.MaxValue)
    {
        var b = new ShortestPathToBuilder(_builder, target, direction, type, maxDistance);
        int distCol = b.CurrentEntityColumn;
        return new GraphTraversal<long>(_tx, _schema, b, row => row.GetInt64(distCol), distCol);
    }

    // ── GC-4: union / coalesce / optional ────────────────────────────────────

    public GraphTraversal<NodeId> Union(params Func<SubTraversal, SubTraversal>[] branches)
        => BuildBranched(branches, BranchedBuilder.Kind.Union);

    public GraphTraversal<NodeId> Coalesce(params Func<SubTraversal, SubTraversal>[] branches)
        => BuildBranched(branches, BranchedBuilder.Kind.Coalesce);

    public GraphTraversal<NodeId> Optional(Func<SubTraversal, SubTraversal> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return BuildBranched(new[] { branch }, BranchedBuilder.Kind.Optional);
    }

    private GraphTraversal<NodeId> BuildBranched(Func<SubTraversal, SubTraversal>[] branches, BranchedBuilder.Kind kind)
    {
        if (branches is null || branches.Length == 0)
            throw new ArgumentException("At least one branch is required.", nameof(branches));
        if (kind == BranchedBuilder.Kind.Optional && branches.Length != 1)
            throw new ArgumentException("Optional accepts exactly one branch.", nameof(branches));

        var captured = branches;
        var b = new BranchedBuilder(_builder, schema =>
        {
            var probes = new CorrelatedInputOperator[captured.Length];
            var ops = new IPhysicalOperator[captured.Length];
            for (int i = 0; i < captured.Length; i++)
            {
                var probe = new CorrelatedInputOperator();
                var seed = new CorrelatedSeedBuilder(probe);
                var start = new SubTraversal(probe, seed, schema, 0);
                var leaf = captured[i](start);
                probes[i] = probe;
                ops[i] = leaf.BuildBranchOperator();
            }
            return (probes, ops);
        }, kind);
        return new GraphTraversal<NodeId>(_tx, _schema, b, row => row.GetNodeId(0), 0);
    }

    public GraphTraversal<string> Values(string key)
    {
        var lookup = new PropertyLookupBuilder(_builder, key);
        int propCol = lookup.PredictedOutputColumnCount - 1;
        return Chain(lookup, row => row.GetString(propCol), _entityColumn);
    }

    // ── GC-6: as / select — tuple-schema extension ──────────────────────────

    /// <summary>
    /// GC-6: Gremlin <c>.as("label")</c> — pin the current entity column under
    /// <paramref name="label"/> so a downstream <see cref="Select(string)"/>
    /// can recover it. Subsequent <c>Out</c>/<c>In</c>/<c>Both</c>/<c>OutE</c>/<c>InE</c>/<c>BothE</c>
    /// steps copy the pinned column through (carried in the operator's tail
    /// tuple slots), so memory grows with the alias count × output rows.
    ///
    /// Limitations: Repeat / ShortestPathTo / Union / Coalesce / Optional /
    /// OutV/InV/OtherV / FilterByKnn rebuild the tuple shape and silently drop
    /// aliases. Re-bind with <c>.As</c> downstream of those steps if needed.
    /// </summary>
    public GraphTraversal<T> As(string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        var next = _aliases is null
            ? new Dictionary<string, int>(capacity: 1)
            : new Dictionary<string, int>(_aliases);
        next[label] = _entityColumn;
        return new GraphTraversal<T>(_tx, _schema, _builder, _projection, _entityColumn, next);
    }

    /// <summary>
    /// GC-6: Gremlin <c>.select("label")</c> — continue the traversal from the
    /// column previously pinned with <see cref="As"/>. The returned traversal
    /// emits <see cref="NodeId"/> regardless of <typeparamref name="T"/> because
    /// pin targets are always entity columns; chain further steps as usual.
    /// </summary>
    public GraphTraversal<NodeId> Select(string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        if (_aliases is null || !_aliases.TryGetValue(label, out var col))
            throw new InvalidOperationException($"Alias '{label}' is not defined. Pin it with .As(\"{label}\") first.");
        // Builder/schema are unchanged; we just re-aim the projection + entity
        // column at the pinned slot. Aliases stay live for chained .Select.
        return new GraphTraversal<NodeId>(_tx, _schema, _builder, row => row.GetNodeId(col), col, _aliases);
    }

    /// <summary>
    /// GC-6: Gremlin <c>.select("a","b",…)</c> — terminal projection that returns
    /// one tuple per row with typed accessors keyed by alias name. The
    /// projection closure receives a <see cref="MatchTuple"/> that resolves
    /// labels to the carried column values without exposing raw tuple indices.
    /// </summary>
    /// <example>
    /// <code>
    /// var pairs = g.V().As("a").Out("KNOWS").As("b")
    ///     .Select(t => (t.Node("a"), t.Node("b")));
    /// </code>
    /// </example>
    public List<TResult> Select<TResult>(Func<MatchTuple, TResult> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (_aliases is null || _aliases.Count == 0)
            throw new InvalidOperationException("Select(projection) requires at least one .As(label) earlier in the chain.");
        var aliases = _aliases;
        var results = new List<TResult>();
        var qr = _tx.Execute(_builder.Build(_schema));
        foreach (var row in qr.Rows())
            results.Add(projection(new MatchTuple(row, aliases)));
        return results;
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
