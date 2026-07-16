using Quiver.Studio.Models;

namespace Quiver.Studio.Services;

public sealed class SugiyamaLayoutService
{
    private const double LayerSpacing = 120.0;
    private const double VertexSpacing = 80.0;
    private const double ComponentGap = 60.0;

    public void Layout(IReadOnlyList<VisualVertex> vertices, IReadOnlyList<VisualEdge> edges)
    {
        if (vertices.Count <= 1)
        {
            if (vertices.Count == 1) { vertices[0].X = 0; vertices[0].Y = 0; }
            return;
        }

        var components = FindComponents(vertices, edges);
        if (components.Count == 1)
        {
            LayoutComponent(vertices, edges);
            CenterGraph(vertices);
            return;
        }

        foreach (var comp in components)
        {
            var compEdges = edges.Where(e =>
                comp.Contains(e.Source) && comp.Contains(e.Target)).ToList();
            LayoutComponent(comp, compEdges);
        }

        PackComponents(components);
        CenterGraph(vertices);
    }

    private static void LayoutComponent(IReadOnlyList<VisualVertex> vertices, IReadOnlyList<VisualEdge> edges)
    {
        if (vertices.Count <= 1)
        {
            if (vertices.Count == 1) { vertices[0].X = 0; vertices[0].Y = 0; }
            return;
        }

        var adj = BuildAdjacency(vertices, edges);
        var reversed = RemoveCycles(vertices, adj);
        var layers = AssignLayers(vertices, adj);
        MinimizeCrossings(layers, adj, passes: 3);
        AssignCoordinates(layers);

        foreach (var (src, tgt) in reversed)
        {
            adj[src].Remove(tgt);
            adj[tgt].Add(src);
        }
    }

    private static Dictionary<VisualVertex, List<VisualVertex>> BuildAdjacency(
        IReadOnlyList<VisualVertex> vertices, IReadOnlyList<VisualEdge> edges)
    {
        var adj = new Dictionary<VisualVertex, List<VisualVertex>>();
        foreach (var n in vertices) adj[n] = [];
        foreach (var e in edges)
        {
            if (adj.ContainsKey(e.Source) && adj.ContainsKey(e.Target))
                adj[e.Source].Add(e.Target);
        }
        return adj;
    }

    private static List<(VisualVertex Src, VisualVertex Tgt)> RemoveCycles(
        IReadOnlyList<VisualVertex> vertices, Dictionary<VisualVertex, List<VisualVertex>> adj)
    {
        var reversed = new List<(VisualVertex, VisualVertex)>();
        var visited = new HashSet<VisualVertex>();
        var inStack = new HashSet<VisualVertex>();

        foreach (var n in vertices)
        {
            if (!visited.Contains(n))
                DfsCycleRemoval(n, adj, visited, inStack, reversed);
        }

        foreach (var (src, tgt) in reversed)
        {
            adj[src].Remove(tgt);
            adj[tgt].Add(src);
        }

        return reversed;
    }

    private static void DfsCycleRemoval(
        VisualVertex vertex,
        Dictionary<VisualVertex, List<VisualVertex>> adj,
        HashSet<VisualVertex> visited,
        HashSet<VisualVertex> inStack,
        List<(VisualVertex, VisualVertex)> reversed)
    {
        visited.Add(vertex);
        inStack.Add(vertex);

        foreach (var neighbor in adj[vertex].ToArray())
        {
            if (!visited.Contains(neighbor))
            {
                DfsCycleRemoval(neighbor, adj, visited, inStack, reversed);
            }
            else if (inStack.Contains(neighbor))
            {
                reversed.Add((vertex, neighbor));
            }
        }

        inStack.Remove(vertex);
    }

    private static List<List<VisualVertex>> AssignLayers(
        IReadOnlyList<VisualVertex> vertices, Dictionary<VisualVertex, List<VisualVertex>> adj)
    {
        var inDegree = new Dictionary<VisualVertex, int>();
        foreach (var n in vertices) inDegree[n] = 0;
        foreach (var (_, neighbors) in adj)
            foreach (var neighbor in neighbors)
                if (inDegree.ContainsKey(neighbor))
                    inDegree[neighbor]++;

        var depth = new Dictionary<VisualVertex, int>();
        var queue = new Queue<VisualVertex>();
        foreach (var n in vertices)
        {
            if (inDegree[n] == 0)
            {
                depth[n] = 0;
                queue.Enqueue(n);
            }
        }

        if (queue.Count == 0)
        {
            depth[vertices[0]] = 0;
            queue.Enqueue(vertices[0]);
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var neighbor in adj[cur])
            {
                var newDepth = depth[cur] + 1;
                if (!depth.TryGetValue(neighbor, out var existing) || newDepth > existing)
                {
                    depth[neighbor] = newDepth;
                    queue.Enqueue(neighbor);
                }
            }
        }

        foreach (var n in vertices)
            depth.TryAdd(n, 0);

        var maxLayer = depth.Values.DefaultIfEmpty(0).Max();
        var layers = new List<List<VisualVertex>>(maxLayer + 1);
        for (var i = 0; i <= maxLayer; i++) layers.Add([]);
        foreach (var n in vertices)
            layers[depth[n]].Add(n);

        return layers;
    }

    private static void MinimizeCrossings(
        List<List<VisualVertex>> layers,
        Dictionary<VisualVertex, List<VisualVertex>> adj,
        int passes)
    {
        var reverseAdj = new Dictionary<VisualVertex, List<VisualVertex>>();
        foreach (var (vertex, _) in adj) reverseAdj[vertex] = [];
        foreach (var (src, neighbors) in adj)
            foreach (var tgt in neighbors)
                if (reverseAdj.ContainsKey(tgt))
                    reverseAdj[tgt].Add(src);

        for (var pass = 0; pass < passes; pass++)
        {
            for (var i = 1; i < layers.Count; i++)
                SortByBarycenter(layers[i], layers[i - 1], reverseAdj);

            for (var i = layers.Count - 2; i >= 0; i--)
                SortByBarycenter(layers[i], layers[i + 1], adj);
        }
    }

    private static void SortByBarycenter(
        List<VisualVertex> layer,
        List<VisualVertex> referenceLayer,
        Dictionary<VisualVertex, List<VisualVertex>> connections)
    {
        var posMap = new Dictionary<VisualVertex, int>();
        for (var i = 0; i < referenceLayer.Count; i++)
            posMap[referenceLayer[i]] = i;

        var barycenters = new Dictionary<VisualVertex, double>();
        foreach (var vertex in layer)
        {
            if (!connections.TryGetValue(vertex, out var neighbors)) continue;
            var positions = new List<int>();
            foreach (var n in neighbors)
                if (posMap.TryGetValue(n, out var pos))
                    positions.Add(pos);

            barycenters[vertex] = positions.Count > 0
                ? positions.Average()
                : double.MaxValue;
        }

        layer.Sort((a, b) =>
        {
            var ba = barycenters.GetValueOrDefault(a, double.MaxValue);
            var bb = barycenters.GetValueOrDefault(b, double.MaxValue);
            return ba.CompareTo(bb);
        });
    }

    private static void AssignCoordinates(List<List<VisualVertex>> layers)
    {
        for (var layerIdx = 0; layerIdx < layers.Count; layerIdx++)
        {
            var layer = layers[layerIdx];
            var totalWidth = (layer.Count - 1) * VertexSpacing;
            var startX = -totalWidth / 2;

            for (var i = 0; i < layer.Count; i++)
            {
                layer[i].X = startX + i * VertexSpacing;
                layer[i].Y = layerIdx * LayerSpacing;
            }
        }
    }

    private static List<List<VisualVertex>> FindComponents(
        IReadOnlyList<VisualVertex> vertices, IReadOnlyList<VisualEdge> edges)
    {
        var adj = new Dictionary<VisualVertex, List<VisualVertex>>();
        foreach (var n in vertices) adj[n] = [];
        foreach (var e in edges)
        {
            if (adj.ContainsKey(e.Source)) adj[e.Source].Add(e.Target);
            if (adj.ContainsKey(e.Target)) adj[e.Target].Add(e.Source);
        }

        var visited = new HashSet<VisualVertex>();
        var components = new List<List<VisualVertex>>();

        foreach (var n in vertices)
        {
            if (!visited.Add(n)) continue;
            var comp = new List<VisualVertex>();
            var queue = new Queue<VisualVertex>();
            queue.Enqueue(n);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                comp.Add(cur);
                foreach (var neighbor in adj[cur])
                    if (visited.Add(neighbor))
                        queue.Enqueue(neighbor);
            }
            components.Add(comp);
        }

        components.Sort((a, b) => b.Count.CompareTo(a.Count));
        return components;
    }

    private static void PackComponents(List<List<VisualVertex>> components)
    {
        var cursorX = 0.0;
        foreach (var comp in components)
        {
            var minX = comp.Min(n => n.X) - 20;
            var maxX = comp.Max(n => n.X) + 20;
            var offsetX = cursorX - minX;
            foreach (var n in comp) n.X += offsetX;
            cursorX += (maxX - minX) + ComponentGap;
        }
    }

    private static void CenterGraph(IReadOnlyList<VisualVertex> vertices)
    {
        var cx = 0.0;
        var cy = 0.0;
        foreach (var n in vertices) { cx += n.X; cy += n.Y; }
        cx /= vertices.Count;
        cy /= vertices.Count;
        foreach (var n in vertices) { n.X -= cx; n.Y -= cy; }
    }
}
