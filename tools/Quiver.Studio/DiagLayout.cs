using Quiver;
using Quiver.Api;
using Quiver.Studio.Models;
using Quiver.Studio.Services;

namespace Quiver.Studio;

internal static class DiagLayout
{
    internal static void Run(string dbPath)
    {
        Console.WriteLine($"=== Layout Diagnostic ===");
        Console.WriteLine($"DB: {dbPath}");

        using var db = QuiverDatabase.Open(dbPath);
        using var tx = db.BeginReadTransaction();

        var vertexMap = new Dictionary<long, VisualVertex>();
        var vertexIds = tx.Query.Vertices().ToList();
        foreach (var nid in vertexIds)
        {
            var label = tx.GetVertexLabel(nid) ?? $"({nid.Sequence})";
            vertexMap[nid.Sequence] = new VisualVertex(nid, label);
        }

        var edges = new List<VisualEdge>();
        var edgeSet = new HashSet<long>();
        foreach (var (_, vn) in vertexMap)
        {
            var edgeEnumerator = tx.EnumerateEdges(vn.Id);
            while (edgeEnumerator.MoveNext())
            {
                var edge = edgeEnumerator.Current;
                if (!edgeSet.Add(edge.Id.Sequence)) continue;
                if (!vertexMap.TryGetValue(edge.Source.Sequence, out var src)) continue;
                if (!vertexMap.TryGetValue(edge.Target.Sequence, out var tgt)) continue;
                var typeName = tx.GetEdgeTypeName(edge.Type) ?? "?";
                edges.Add(new VisualEdge(edge.Id, src, tgt, typeName));
            }
        }

        var vertices = vertexMap.Values.ToList();
        Console.WriteLine($"Vertices: {vertices.Count}, Edges: {edges.Count}");
        Console.WriteLine();

        // 次数の分析
        var degree = new Dictionary<string, int>();
        foreach (var n in vertices) degree[n.Label + "#" + n.Id.Sequence] = 0;
        foreach (var e in edges)
        {
            degree[e.Source.Label + "#" + e.Source.Id.Sequence]++;
            degree[e.Target.Label + "#" + e.Target.Id.Sequence]++;
        }
        Console.WriteLine("--- Degree ---");
        foreach (var (k, v) in degree.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {k}: degree={v}");
        Console.WriteLine();

        // レイアウトの実行
        var layout = new GraphLayoutService();
        layout.Layout(vertices, edges);

        Console.WriteLine("--- Layout Result ---");
        var minX = vertices.Min(n => n.X);
        var maxX = vertices.Max(n => n.X);
        var minY = vertices.Min(n => n.Y);
        var maxY = vertices.Max(n => n.Y);
        Console.WriteLine($"BBox: X=[{minX:F1}, {maxX:F1}] Y=[{minY:F1}, {maxY:F1}]");
        Console.WriteLine($"Span: {maxX - minX:F1} x {maxY - minY:F1}");
        Console.WriteLine();

        foreach (var n in vertices.OrderBy(n => n.Label).ThenBy(n => n.Id.Sequence))
            Console.WriteLine($"  {n.Label,-8} #{n.Id.Sequence,-3} ({n.X:F1}, {n.Y:F1})");
        Console.WriteLine();

        // エッジ長の集計
        Console.WriteLine("--- Edge Lengths ---");
        var lengths = edges.Select(e =>
        {
            var dx = e.Target.X - e.Source.X;
            var dy = e.Target.Y - e.Source.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }).OrderBy(x => x).ToList();
        Console.WriteLine($"  min={lengths[0]:F1}  median={lengths[lengths.Count / 2]:F1}  max={lengths[^1]:F1}");
        Console.WriteLine($"  IdealEdgeLength=100");

        // 1400x700 ビューポートで FitToContent を模擬する。
        var viewW = 1400.0;
        var viewH = 700.0;
        var margin = 40.0;
        var r = 20.0;
        var gw = (maxX + r) - (minX - r);
        var gh = (maxY + r) - (minY - r);
        var zoom = Math.Min((viewW - margin * 2) / gw, (viewH - margin * 2) / gh);
        zoom = Math.Clamp(zoom, 0.05, 10.0);
        Console.WriteLine();
        Console.WriteLine($"--- Simulated FitToContent ({viewW}x{viewH}) ---");
        Console.WriteLine($"  Graph size: {gw:F1} x {gh:F1}");
        Console.WriteLine($"  Zoom: {zoom:F4}");
        Console.WriteLine($"  Vertex radius on screen: {r * zoom:F1}px");
    }
}
