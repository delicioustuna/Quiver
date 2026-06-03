using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Operators;

/// <summary>
/// TS-6: 全 operator micro-benchmark で共通利用する小さな seed graph。
/// 各 operator bench は <see cref="GlobalSetup"/> 相当のフェーズで
/// <see cref="Open"/> を 1 回呼び、計測区間は warm transaction 上で
/// operator パイプラインを 1 回 drain する形に統一する。
///
/// グラフ規模は意図的に小さく (200 nodes / 400 edges) 保つ:
///   - 目的は「20% 以上の劣化を見逃さない baseline を全 operator に持つ」こと。
///     hot path 性能特性は既存ベンチ (BulkLoad / OneHop / FilterChainExpand 等) が
///     担当しているので、ここでは regression sentinel としての 1 数値があれば十分。
///   - 小さい seed なら 29 bench × ~1 秒の baseline run が現実的になる。
/// </summary>
internal sealed class OperatorBenchSeed : IDisposable
{
    public const int NodeCount = 200;
    public const int EdgesPerNode = 2;

    public string Dir { get; }
    public GraphDatabase Db { get; }
    public NodeId[] PersonNodes { get; }
    public NodeId[] MovieNodes { get; }
    public RelationshipId[] Relationships { get; }
    public LabelId PersonLabel { get; }
    public LabelId MovieLabel { get; }
    public RelationshipTypeId KnowsType { get; }
    public PropertyKeyId NameKey { get; }
    public PropertyKeyId ValueKey { get; }
    public PropertyKeyId WeightKey { get; }
    public IGraphTransaction ReadTx { get; }

    public OperatorBenchSeed(string tag)
    {
        Dir = BenchTempDir.Create("opbench_" + tag);
        Db = GraphDatabase.Open(System.IO.Path.Combine(Dir, "graph.quiver"));

        Db.Schema.CreateIndex("idx_value", "Person", "value", IndexKind.Int64Equality);
        Db.Schema.CreateIndex("idx_name", "Person", "name", IndexKind.StringEquality);

        NameKey = Db.Schema.GetOrCreatePropertyKey("name");
        ValueKey = Db.Schema.GetOrCreatePropertyKey("value");
        WeightKey = Db.Schema.GetOrCreatePropertyKey("weight");

        PersonNodes = new NodeId[NodeCount];
        MovieNodes = new NodeId[NodeCount];
        Relationships = new RelationshipId[NodeCount * EdgesPerNode];

        var rng = new Random(2026);
        using (var tx = Db.BeginTransaction())
        {
            for (int i = 0; i < NodeCount; i++)
            {
                var p = tx.CreateNode("Person");
                PersonNodes[i] = p;
                tx.SetProperty(p, "value", PropertyValue.FromInt64(i));
                tx.SetProperty(p, "name", PropertyValue.FromString("name-" + i.ToString("D4")));
                tx.IndexInsert("idx_value", (long)i, p);
                tx.IndexInsert("idx_name", "name-" + i.ToString("D4"), p);

                var m = tx.CreateNode("Movie");
                MovieNodes[i] = m;
            }
            int relIdx = 0;
            for (int i = 0; i < NodeCount; i++)
            {
                for (int e = 0; e < EdgesPerNode; e++)
                {
                    int target = rng.Next(NodeCount);
                    var r = tx.CreateRelationship(PersonNodes[i], PersonNodes[target], "KNOWS");
                    tx.SetProperty(r, "weight", PropertyValue.FromDouble(1.0 + (i % 5)));
                    Relationships[relIdx++] = r;
                }
            }
            tx.Commit();
        }

        PersonLabel = Db.Schema.GetOrCreateLabel("Person");
        MovieLabel = Db.Schema.GetOrCreateLabel("Movie");
        KnowsType = Db.Schema.GetOrCreateRelationshipType("KNOWS");

        ReadTx = Db.BeginReadOnlyTransaction();
    }

    public void Dispose()
    {
        ReadTx?.Dispose();
        Db?.Dispose();
        BenchTempDir.Delete(Dir);
    }
}

/// <summary>
/// Source operator that re-emits a pre-built NodeId[] each Open() — used by
/// expand/filter/etc benches as a warm input that incurs no per-iteration
/// allocations beyond the operator under test.
/// </summary>
internal sealed class NodeArraySource : IPhysicalOperator
{
    private readonly NodeId[] _nodes;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public NodeArraySource(NodeId[] nodes) => _nodes = nodes;

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

internal sealed class RelArraySource : IPhysicalOperator
{
    private readonly RelationshipId[] _rels;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];
    public RelArraySource(RelationshipId[] rels) => _rels = rels;
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

internal sealed class NodePairSource : IPhysicalOperator
{
    private readonly (NodeId src, NodeId tgt)[] _pairs;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];
    public NodePairSource((NodeId src, NodeId tgt)[] pairs) => _pairs = pairs;
    public TupleSchema Schema { get; } = new([
        new ColumnDefinition("s", TupleSlotType.NodeId),
        new ColumnDefinition("t", TupleSlotType.NodeId),
    ]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);
    public void Open(ITransaction tx) { _index = -1; }
    public bool MoveNext()
    {
        if (++_index >= _pairs.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _pairs[_index].src.Value };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _pairs[_index].tgt.Value };
        return true;
    }
    public void Dispose() { }
}

internal sealed class DepthRowSource : IPhysicalOperator
{
    private readonly (long nodeId, long depth)[] _rows;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];
    public DepthRowSource((long nodeId, long depth)[] rows) => _rows = rows;
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

internal sealed class EvenIdPredicate : IPredicate
{
    public bool Evaluate(in TupleRef tuple, ITransaction tx)
        => (tuple[0].LongValue & 1) == 0;
}

internal sealed class DoubleLongCompute : IProjectionCompute
{
    public TupleSlot Compute(in TupleRef tuple, ITransaction tx)
        => new TupleSlot { Type = TupleSlotType.Int64, LongValue = tuple[0].LongValue * 2 };
}

internal static class OperatorBenchDrain
{
    /// <summary>
    /// Execute <paramref name="op"/> against <paramref name="tx"/> and count the
    /// materialized rows. Goes through <see cref="IGraphTransaction.Execute"/>
    /// rather than direct Open/MoveNext so the bench measures the same path
    /// used by client code (the Volcano iteration overhead is identical;
    /// QueryResult materializes into a list but the loop dominates).
    /// </summary>
    public static int Drain(IPhysicalOperator op, IGraphTransaction tx)
    {
        using var result = tx.Execute(op);
        int n = 0;
        foreach (var _ in result.Rows()) n++;
        return n;
    }
}
