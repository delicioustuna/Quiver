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

        using var db = GraphDatabase.Open(dbPath);
        using var tx = db.BeginReadOnlyTransaction();

        var nodeMap = new Dictionary<long, VisualNode>();
        var nodeIds = tx.G(db.Schema).Nodes().ToList();
        foreach (var nid in nodeIds)
        {
            var label = tx.GetNodeLabel(nid) ?? $"({nid.Sequence})";
            nodeMap[nid.Sequence] = new VisualNode(nid, label);
        }

        var edges = new List<VisualEdge>();
        var edgeSet = new HashSet<long>();
        foreach (var (_, vn) in nodeMap)
        {
            var rels = tx.EnumerateRelationships(vn.Id);
            while (rels.MoveNext())
            {
                var rel = rels.Current;
                if (!edgeSet.Add(rel.Id.Sequence)) continue;
                if (!nodeMap.TryGetValue(rel.Source.Sequence, out var src)) continue;
                if (!nodeMap.TryGetValue(rel.Target.Sequence, out var tgt)) continue;
                var typeName = tx.GetRelationshipTypeName(rel.Type) ?? "?";
                edges.Add(new VisualEdge(rel.Id, src, tgt, typeName));
            }
        }

        var nodes = nodeMap.Values.ToList();
        Console.WriteLine($"Nodes: {nodes.Count}, Edges: {edges.Count}");
        Console.WriteLine();

        // Degree analysis
        var degree = new Dictionary<string, int>();
        foreach (var n in nodes) degree[n.Label + "#" + n.Id.Sequence] = 0;
        foreach (var e in edges)
        {
            degree[e.Source.Label + "#" + e.Source.Id.Sequence]++;
            degree[e.Target.Label + "#" + e.Target.Id.Sequence]++;
        }
        Console.WriteLine("--- Degree ---");
        foreach (var (k, v) in degree.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {k}: degree={v}");
        Console.WriteLine();

        // Run layout
        var layout = new GraphLayoutService();
        layout.Layout(nodes, edges);

        Console.WriteLine("--- Layout Result ---");
        var minX = nodes.Min(n => n.X);
        var maxX = nodes.Max(n => n.X);
        var minY = nodes.Min(n => n.Y);
        var maxY = nodes.Max(n => n.Y);
        Console.WriteLine($"BBox: X=[{minX:F1}, {maxX:F1}] Y=[{minY:F1}, {maxY:F1}]");
        Console.WriteLine($"Span: {maxX - minX:F1} x {maxY - minY:F1}");
        Console.WriteLine();

        foreach (var n in nodes.OrderBy(n => n.Label).ThenBy(n => n.Id.Sequence))
            Console.WriteLine($"  {n.Label,-8} #{n.Id.Sequence,-3} ({n.X:F1}, {n.Y:F1})");
        Console.WriteLine();

        // Edge lengths
        Console.WriteLine("--- Edge Lengths ---");
        var lengths = edges.Select(e =>
        {
            var dx = e.Target.X - e.Source.X;
            var dy = e.Target.Y - e.Source.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }).OrderBy(x => x).ToList();
        Console.WriteLine($"  min={lengths[0]:F1}  median={lengths[lengths.Count / 2]:F1}  max={lengths[^1]:F1}");
        Console.WriteLine($"  IdealEdgeLength=100");

        // Simulated FitToContent for 1400x700 viewport
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
        Console.WriteLine($"  Node radius on screen: {r * zoom:F1}px");
    }
}
