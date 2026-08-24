using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks.Operators;

/// <summary>
/// 全 operator micro-benchmark で共通利用する小さな seed graph。
/// 各 operator bench は <see cref="GlobalSetup"/> 相当のフェーズで
/// <see cref="Open"/> を 1 回呼び、計測区間は warm transaction 上で
/// operator パイプラインを 1 回 drain する形に統一する。
///
/// グラフ規模は意図的に小さく (200 vertices / 400 edges) 保つ:
///   - 目的は「20% 以上の劣化を見逃さない baseline を全 operator に持つ」こと。
///     hot path 性能特性は既存ベンチ (BulkLoad / OneHop / FilterChainExpand 等) が
///     担当しているので、ここでは regression sentinel としての 1 数値があれば十分。
///   - 小さい seed なら 29 bench × ~1 秒の baseline run が現実的になる。
/// </summary>
internal sealed class OperatorBenchSeed : IDisposable
{
    public const int VertexCount = 200;
    public const int EdgesPerVertex = 2;

    public string Dir { get; }
    public YatagarasuDatabase Db { get; }
    public VertexId[] PersonVertices { get; }
    public VertexId[] MovieVertices { get; }
    public EdgeId[] Edges { get; }
    public LabelId PersonLabel { get; }
    public LabelId MovieLabel { get; }
    public EdgeTypeId KnowsType { get; }
    public PropertyKeyId NameKey { get; }
    public PropertyKeyId ValueKey { get; }
    public PropertyKeyId WeightKey { get; }
    public ScalarIndexDefinition ValueIndex { get; }
    public ScalarIndexDefinition NameIndex { get; }
    public IReadTransaction ReadTx { get; }

    public OperatorBenchSeed(string tag)
    {
        Dir = BenchTempDir.Create("opbench_" + tag);
        Db = YatagarasuDatabase.Open(System.IO.Path.Combine(Dir, "graph.yata"));

        Db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_value", new PropertyTarget(PropertyOwnerKind.Vertex, "value", "Person"), IndexKind.Int64Equality)));
        Db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));

        NameKey = Db.EditSchema(schema => schema.GetOrCreatePropertyKey("name"));
        ValueKey = Db.EditSchema(schema => schema.GetOrCreatePropertyKey("value"));
        WeightKey = Db.EditSchema(schema => schema.GetOrCreatePropertyKey("weight"));

        PersonVertices = new VertexId[VertexCount];
        MovieVertices = new VertexId[VertexCount];
        Edges = new EdgeId[VertexCount * EdgesPerVertex];

        var rng = new Random(2026);
        using (var tx = Db.BeginWriteTransaction())
        {
            for (int i = 0; i < VertexCount; i++)
            {
                var p = tx.CreateVertex("Person");
                PersonVertices[i] = p;
                tx.SetProperty(p, "value", PropertyValue.FromInt64(i));
                tx.SetProperty(p, "name", PropertyValue.FromString("name-" + i.ToString("D4")));
                tx.SetIndexedProperty("idx_value", (long)i, p);
                tx.SetIndexedProperty("idx_name", "name-" + i.ToString("D4"), p);

                var m = tx.CreateVertex("Movie");
                MovieVertices[i] = m;
            }
            int edgeIdx = 0;
            for (int i = 0; i < VertexCount; i++)
            {
                for (int e = 0; e < EdgesPerVertex; e++)
                {
                    int target = rng.Next(VertexCount);
                    var r = tx.CreateEdge(PersonVertices[i], PersonVertices[target], "KNOWS");
                    tx.SetProperty(r, "weight", PropertyValue.FromDouble(1.0 + (i % 5)));
                    Edges[edgeIdx++] = r;
                }
            }
            tx.Commit();
        }

        PersonLabel = Db.EditSchema(schema => schema.GetOrCreateLabel("Person"));
        MovieLabel = Db.EditSchema(schema => schema.GetOrCreateLabel("Movie"));
        KnowsType = Db.EditSchema(schema => schema.GetOrCreateEdgeType("KNOWS"));
        ValueIndex = Db.Schema.ListIndexes()
            .Single(index => index.Name == "idx_value")
            .Definition as ScalarIndexDefinition
            ?? throw new InvalidOperationException("idx_value is not a scalar index.");
        NameIndex = Db.Schema.ListIndexes()
            .Single(index => index.Name == "idx_name")
            .Definition as ScalarIndexDefinition
            ?? throw new InvalidOperationException("idx_name is not a scalar index.");

        ReadTx = Db.BeginReadTransaction();
    }

    public void Dispose()
    {
        ReadTx?.Dispose();
        Db?.Dispose();
        BenchTempDir.Delete(Dir);
    }
}

/// <summary>
/// Source operator that re-emits a pre-built VertexId[] each Open() — used by
/// expand/filter/etc benches as a warm input that incurs no per-iteration
/// allocations beyond the operator under test.
/// </summary>
internal sealed class VertexArraySource : IPhysicalOperator
{
    private readonly VertexId[] _vertices;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public VertexArraySource(VertexId[] vertices) => _vertices = vertices;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _index = -1; }
    public bool MoveNext()
    {
        if (++_index >= _vertices.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _vertices[_index].Value };
        return true;
    }
    public void Dispose() { }
}

internal sealed class RelArraySource : IPhysicalOperator
{
    private readonly EdgeId[] _edges;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];
    public RelArraySource(EdgeId[] edges) => _edges = edges;
    public TupleSchema Schema { get; } = new([new ColumnDefinition("edge", TupleSlotType.EdgeId)]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);
    public void Open(ITransaction tx) { _index = -1; }
    public bool MoveNext()
    {
        if (++_index >= _edges.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.EdgeId, LongValue = _edges[_index].Value };
        return true;
    }
    public void Dispose() { }
}

internal sealed class VertexPairSource : IPhysicalOperator
{
    private readonly (VertexId src, VertexId tgt)[] _pairs;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];
    public VertexPairSource((VertexId src, VertexId tgt)[] pairs) => _pairs = pairs;
    public TupleSchema Schema { get; } = new([
        new ColumnDefinition("s", TupleSlotType.VertexId),
        new ColumnDefinition("t", TupleSlotType.VertexId),
    ]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);
    public void Open(ITransaction tx) { _index = -1; }
    public bool MoveNext()
    {
        if (++_index >= _pairs.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _pairs[_index].src.Value };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _pairs[_index].tgt.Value };
        return true;
    }
    public void Dispose() { }
}

internal sealed class DepthRowSource : IPhysicalOperator
{
    private readonly (long vertexId, long depth)[] _rows;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];
    public DepthRowSource((long vertexId, long depth)[] rows) => _rows = rows;
    public TupleSchema Schema { get; } = new([
        new ColumnDefinition("vertexId", TupleSlotType.VertexId),
        new ColumnDefinition("depth", TupleSlotType.Int64),
    ]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);
    public void Open(ITransaction tx) { _index = -1; }
    public bool MoveNext()
    {
        if (++_index >= _rows.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _rows[_index].vertexId };
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
    /// materialized rows. Goes through <see cref="IWriteTransaction.Execute"/>
    /// rather than direct Open/MoveNext so the bench measures the same path
    /// used by client code (the Volcano iteration overhead is identical;
    /// QueryResult materializes into a list but the loop dominates).
    /// </summary>
    public static int Drain(IPhysicalOperator op, IReadTransaction tx)
    {
        using var result = tx.Execute(op);
        int n = 0;
        foreach (var _ in result.Rows()) n++;
        return n;
    }
}
