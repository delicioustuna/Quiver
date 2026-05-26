using Quiver;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Transactions;

namespace Quiver.Operators.Tests.Support;

/// <summary>
/// TS-2: shared DB-backed fixture for operator unit tests. Each fixture opens
/// a fresh on-disk DB under %TEMP%; the directory is removed on Dispose so the
/// test sandbox does not accumulate. Tests that need only a no-graph schema
/// can use <see cref="OpenEmpty"/>; tests that need a seeded graph can pass
/// a seed delegate to <see cref="Open(Action{IGraphTransaction})"/>.
/// </summary>
internal sealed class OperatorTestFixture : IDisposable
{
    public string Dir { get; }
    public GraphDatabase Db { get; }

    private OperatorTestFixture(string dir, GraphDatabase db)
    {
        Dir = dir;
        Db = db;
    }

    public static OperatorTestFixture OpenEmpty(string tag = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"quiver_ts2_{tag}_{Guid.NewGuid():N}");
        var db = GraphDatabase.Open(dir);
        return new OperatorTestFixture(dir, db);
    }

    public static OperatorTestFixture Open(Action<IGraphTransaction> seed, string tag = "")
    {
        var fx = OpenEmpty(tag);
        using (var tx = fx.Db.BeginTransaction())
        {
            seed(tx);
            tx.Commit();
        }
        return fx;
    }

    public void Dispose()
    {
        Db.Dispose();
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        }
        catch { /* swallow — best-effort sandbox cleanup */ }
    }
}

/// <summary>
/// Minimal source operator producing a fixed sequence of NodeId rows.
/// Used by mock-only tests that don't need a transaction context.
/// </summary>
internal sealed class FixedNodeListOperator : IPhysicalOperator
{
    private readonly NodeId[] _nodes;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FixedNodeListOperator(params NodeId[] nodes) => _nodes = nodes;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _index = -1; }

    public bool MoveNext()
    {
        if (++_index >= _nodes.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _nodes[_index].Value };
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// Source operator producing (nodeId, depth) rows — used by FrontierLimit tests.
/// </summary>
internal sealed class DepthTaggedSourceOperator : IPhysicalOperator
{
    private readonly (long nodeId, long depth)[] _rows;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    public DepthTaggedSourceOperator(params (long nodeId, long depth)[] rows) => _rows = rows;

    public TupleSchema Schema { get; } = new([
        new ColumnDefinition("nodeId", TupleSlotType.NodeId),
        new ColumnDefinition("depth", TupleSlotType.Int64),
    ]);

    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _index = -1; }

    public bool MoveNext()
    {
        if (++_index >= _rows.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _rows[_index].nodeId };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.Int64, LongValue = _rows[_index].depth };
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// Source operator emitting a single (source, target) pair — used by
/// shortest-path / bidirectional-expand style operators that consume
/// two NodeId columns.
/// </summary>
internal sealed class PairSourceOperator : IPhysicalOperator
{
    private readonly NodeId _src;
    private readonly NodeId _tgt;
    private bool _emitted;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    public PairSourceOperator(NodeId src, NodeId tgt) { _src = src; _tgt = tgt; }

    public TupleSchema Schema { get; } = new([
        new ColumnDefinition("s", TupleSlotType.NodeId),
        new ColumnDefinition("t", TupleSlotType.NodeId),
    ]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _emitted = false; }

    public bool MoveNext()
    {
        if (_emitted) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _src.Value };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _tgt.Value };
        _emitted = true;
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// Source operator emitting a sequence of relationship-id rows — used by
/// <see cref="RelationshipEndpointOperator"/> tests which take a rel column.
/// </summary>
internal sealed class FixedRelationshipListOperator : IPhysicalOperator
{
    private readonly RelationshipId[] _rels;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FixedRelationshipListOperator(params RelationshipId[] rels) => _rels = rels;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("rel", TupleSlotType.RelationshipId)]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _index = -1; }

    public bool MoveNext()
    {
        if (++_index >= _rels.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.RelationshipId, LongValue = _rels[_index].Value };
        return true;
    }

    public void Dispose() { }
}

/// <summary>Predicate: tuple[0].LongValue is even.</summary>
internal sealed class EvenNodeIdPredicate : IPredicate
{
    public bool Evaluate(in TupleRef tuple, ITransaction tx) => tuple[0].LongValue % 2 == 0;
}

/// <summary>Predicate that always returns true. Counts invocations.</summary>
internal sealed class AlwaysTruePredicate : IPredicate
{
    public int Calls;
    public bool Evaluate(in TupleRef tuple, ITransaction tx) { Calls++; return true; }
}

/// <summary>Predicate that always returns false.</summary>
internal sealed class AlwaysFalsePredicate : IPredicate
{
    public bool Evaluate(in TupleRef tuple, ITransaction tx) => false;
}

/// <summary>Project compute: doubles the LongValue of column 0.</summary>
internal sealed class DoubleValueCompute : IProjectionCompute
{
    public TupleSlot Compute(in TupleRef tuple, ITransaction tx)
        => new TupleSlot { Type = TupleSlotType.Int64, LongValue = tuple[0].LongValue * 2 };
}

/// <summary>Common helper for collecting all rows from an operator.</summary>
internal static class OperatorCollect
{
    public static List<long> Collect(IPhysicalOperator op)
    {
        var result = new List<long>();
        while (op.MoveNext())
            result.Add(op.Current[0].LongValue);
        return result;
    }

    public static List<long[]> CollectAllColumns(IPhysicalOperator op)
    {
        var result = new List<long[]>();
        while (op.MoveNext())
        {
            var cur = op.Current;
            var row = new long[cur.ColumnCount];
            for (int i = 0; i < cur.ColumnCount; i++) row[i] = cur[i].LongValue;
            result.Add(row);
        }
        return result;
    }
}
