using BenchmarkDotNet.Attributes;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// 重み付き最短経路: BFS ホップ数最短 (<see cref="ShortestPathOperator"/>) と
/// 重み付き Dijkstra / A* (<see cref="WeightedShortestPathOperator"/>) の比較。
/// Setup: GridSize×GridSize の格子グラフを各エッジ重み 1.0 で構築。
/// クエリ: 左上隅から右下隅までの最短経路。A* は格子座標から Euclidean
/// ヒューリスティックを与え、展開Vertex数の削減で Dijkstra より速くなることを示す。
/// </summary>
[MemoryDiagnoser]
public class WeightedShortestPathBenchmarks
{
    [Params(20, 40)]
    public int GridSize { get; set; }

    private QuiverDatabase _db = null!;
    private string _dbPath = null!;
    private IReadTransaction _readTx = null!;
    private PropertyChainWeightProvider _weightProvider = null!;
    private readonly Dictionary<long, (double R, double C)> _coords = [];
    private VertexId _srcVertex;
    private VertexId _tgtVertex;
    private Func<VertexId, double> _heuristic = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = BenchTempDir.Create("wsp");
        int n = GridSize;
        var grid = new VertexId[n, n];

        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver")))
        using (var tx = db.BeginWriteTransaction())
        {
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    grid[r, c] = tx.CreateVertex("Cell");
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    if (c + 1 < n)
                        tx.SetProperty(tx.CreateEdge(grid[r, c], grid[r, c + 1], "K"),
                            "w", PropertyValue.FromDouble(1.0));
                    if (r + 1 < n)
                        tx.SetProperty(tx.CreateEdge(grid[r, c], grid[r + 1, c], "K"),
                            "w", PropertyValue.FromDouble(1.0));
                }
            tx.Commit();
        }

        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                _coords[grid[r, c].Value] = (r, c);

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dbPath, "graph.quiver"));
        _readTx = _db.BeginWriteTransaction();
        var keyId = _db.EditSchema(schema => schema.GetOrCreatePropertyKey("w"));
        _weightProvider = new PropertyChainWeightProvider(keyId);

        _srcVertex = grid[0, 0];
        _tgtVertex = grid[n - 1, n - 1];
        var (tgtR, tgtC) = _coords[_tgtVertex.Value];
        _heuristic = vertex =>
        {
            var (r, c) = _coords[vertex.Value];
            double dr = r - tgtR, dc = c - tgtC;
            return Math.Sqrt(dr * dr + dc * dc);
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readTx?.Dispose();
        _db?.Dispose();
        BenchTempDir.Delete(_dbPath);
    }

    [Benchmark(Baseline = true, Description = "ShortestPath (BFS hop-count)")]
    public long ShortestPathBfs()
    {
        var plan = new ShortestPathOperator(
            new PairVertexSource(_srcVertex, _tgtVertex), 0, 1, Direction.Outgoing, typeFilter: null);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced == 0 ? -1 : result.Rows().First().GetInt64(2);
    }

    [Benchmark(Description = "Weighted Dijkstra")]
    public double Dijkstra()
    {
        var plan = new WeightedShortestPathOperator(
            new PairVertexSource(_srcVertex, _tgtVertex), 0, 1, Direction.Outgoing, null, _weightProvider);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced == 0 ? -1 : result.Rows().First().GetDouble(2);
    }

    [Benchmark(Description = "Weighted A* (Euclidean heuristic)")]
    public double AStar()
    {
        var plan = new WeightedShortestPathOperator(
            new PairVertexSource(_srcVertex, _tgtVertex), 0, 1, Direction.Outgoing, null,
            _weightProvider, _heuristic);
        using var result = _readTx.Execute(plan);
        return result.Statistics.RowsProduced == 0 ? -1 : result.Rows().First().GetDouble(2);
    }
}
