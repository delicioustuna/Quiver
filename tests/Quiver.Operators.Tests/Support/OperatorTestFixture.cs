using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Transactions;

namespace Quiver.Query.Physical.Tests.Support;

/// <summary>
/// 演算子のユニットテストで共有するデータベース付きフィクスチャ。
/// 各インスタンスは %TEMP% 配下に新しいオンディスクデータベースを開き、
/// 破棄時にディレクトリを削除する。
/// グラフが不要なテストは <see cref="OpenEmpty"/> を使い、
/// 初期データが必要なテストは <see cref="Open(Action{IWriteTransaction})"/> に
/// シード処理を渡す。
/// </summary>
internal sealed class OperatorTestFixture : IDisposable
{
    public string Dir { get; }
    public QuiverDatabase Db { get; }

    private OperatorTestFixture(string dir, QuiverDatabase db)
    {
        Dir = dir;
        Db = db;
    }

    public static OperatorTestFixture OpenEmpty(string tag = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"quiver_ts2_{tag}_{Guid.NewGuid():N}");
        var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
        return new OperatorTestFixture(dir, db);
    }

    public static OperatorTestFixture Open(Action<IWriteTransaction> seed, string tag = "")
    {
        var fx = OpenEmpty(tag);
        using (var tx = fx.Db.BeginWriteTransaction())
        {
            seed(tx);
            tx.Commit();
        }
        return fx;
    }

    public TResult EditSchema<TResult>(Func<ISchemaEditor, TResult> edit)
    {
        using var tx = Db.BeginWriteTransaction();
        TResult result = edit(tx.EditSchema);
        tx.Commit();
        return result;
    }

    public void EditSchema(Action<ISchemaEditor> edit)
    {
        using var tx = Db.BeginWriteTransaction();
        edit(tx.EditSchema);
        tx.Commit();
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
/// VertexId 行の固定列を生成する最小の入力演算子。
/// トランザクションコンテキストが不要なモックテストで使用する。
/// </summary>
internal sealed class FixedVertexListOperator : IPhysicalOperator
{
    private readonly VertexId[] _vertices;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FixedVertexListOperator(params VertexId[] vertices) => _vertices = vertices;

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

/// <summary>
/// (vertexId, depth) 行を生成する入力演算子。FrontierLimit のテストで使用する。
/// </summary>
internal sealed class DepthTaggedSourceOperator : IPhysicalOperator
{
    private readonly (long vertexId, long depth)[] _rows;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    public DepthTaggedSourceOperator(params (long vertexId, long depth)[] rows) => _rows = rows;

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

/// <summary>
/// (source, target) の組を 1 件出力する入力演算子。
/// VertexId 2 列を受け取る最短経路および双方向展開演算子で使用する。
/// </summary>
internal sealed class PairSourceOperator : IPhysicalOperator
{
    private readonly VertexId _src;
    private readonly VertexId _tgt;
    private bool _emitted;
    private readonly TupleSlot[] _buffer = new TupleSlot[2];

    public PairSourceOperator(VertexId src, VertexId tgt) { _src = src; _tgt = tgt; }

    public TupleSchema Schema { get; } = new([
        new ColumnDefinition("s", TupleSlotType.VertexId),
        new ColumnDefinition("t", TupleSlotType.VertexId),
    ]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _emitted = false; }

    public bool MoveNext()
    {
        if (_emitted) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _src.Value };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _tgt.Value };
        _emitted = true;
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// Edge ID 行の列を出力する入力演算子。
/// <see cref="EdgeEndpointOperator"/> tests which take a edge column.
/// </summary>
internal sealed class FixedEdgeListOperator : IPhysicalOperator
{
    private readonly EdgeId[] _edges;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FixedEdgeListOperator(params EdgeId[] edges) => _edges = edges;

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

/// <summary>Predicate: tuple[0].LongValue is even.</summary>
internal sealed class EvenVertexIdPredicate : IPredicate
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
