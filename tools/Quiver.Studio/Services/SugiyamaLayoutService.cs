using Quiver.Studio.Models;

namespace Quiver.Studio.Services;

public sealed class SugiyamaLayoutService
{
    private const double LayerSpacing = 120.0;
    private const double NodeSpacing = 80.0;
    private const double ComponentGap = 60.0;

    public void Layout(IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges)
    {
        if (nodes.Count <= 1)
        {
            if (nodes.Count == 1) { nodes[0].X = 0; nodes[0].Y = 0; }
            return;
        }

        var components = FindComponents(nodes, edges);
        if (components.Count == 1)
        {
            LayoutComponent(nodes, edges);
            CenterGraph(nodes);
            return;
        }

        foreach (var comp in components)
        {
            var compEdges = edges.Where(e =>
                comp.Contains(e.Source) && comp.Contains(e.Target)).ToList();
            LayoutComponent(comp, compEdges);
        }

        PackComponents(components);
        CenterGraph(nodes);
    }

    private static void LayoutComponent(IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges)
    {
        if (nodes.Count <= 1)
        {
            if (nodes.Count == 1) { nodes[0].X = 0; nodes[0].Y = 0; }
            return;
        }

        var adj = BuildAdjacency(nodes, edges);
        var reversed = RemoveCycles(nodes, adj);
        var layers = AssignLayers(nodes, adj);
        MinimizeCrossings(layers, adj, passes: 3);
        AssignCoordinates(layers);

        foreach (var (src, tgt) in reversed)
        {
            adj[src].Remove(tgt);
            adj[tgt].Add(src);
        }
    }

    private static Dictionary<VisualNode, List<VisualNode>> BuildAdjacency(
        IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges)
    {
        var adj = new Dictionary<VisualNode, List<VisualNode>>();
        foreach (var n in nodes) adj[n] = [];
        foreach (var e in edges)
        {
            if (adj.ContainsKey(e.Source) && adj.ContainsKey(e.Target))
                adj[e.Source].Add(e.Target);
        }
        return adj;
    }

    private static List<(VisualNode Src, VisualNode Tgt)> RemoveCycles(
        IReadOnlyList<VisualNode> nodes, Dictionary<VisualNode, List<VisualNode>> adj)
    {
        var reversed = new List<(VisualNode, VisualNode)>();
        var visited = new HashSet<VisualNode>();
        var inStack = new HashSet<VisualNode>();

        foreach (var n in nodes)
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
        VisualNode node,
        Dictionary<VisualNode, List<VisualNode>> adj,
        HashSet<VisualNode> visited,
        HashSet<VisualNode> inStack,
        List<(VisualNode, VisualNode)> reversed)
    {
        visited.Add(node);
        inStack.Add(node);

        foreach (var neighbor in adj[node].ToArray())
        {
            if (!visited.Contains(neighbor))
            {
                DfsCycleRemoval(neighbor, adj, visited, inStack, reversed);
            }
            else if (inStack.Contains(neighbor))
            {
                reversed.Add((node, neighbor));
            }
        }

        inStack.Remove(node);
    }

    private static List<List<VisualNode>> AssignLayers(
        IReadOnlyList<VisualNode> nodes, Dictionary<VisualNode, List<VisualNode>> adj)
    {
        var inDegree = new Dictionary<VisualNode, int>();
        foreach (var n in nodes) inDegree[n] = 0;
        foreach (var (_, neighbors) in adj)
            foreach (var neighbor in neighbors)
                if (inDegree.ContainsKey(neighbor))
                    inDegree[neighbor]++;

        var depth = new Dictionary<VisualNode, int>();
        var queue = new Queue<VisualNode>();
        foreach (var n in nodes)
        {
            if (inDegree[n] == 0)
            {
                depth[n] = 0;
                queue.Enqueue(n);
            }
        }

        if (queue.Count == 0)
        {
            depth[nodes[0]] = 0;
            queue.Enqueue(nodes[0]);
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

        foreach (var n in nodes)
            depth.TryAdd(n, 0);

        var maxLayer = depth.Values.DefaultIfEmpty(0).Max();
        var layers = new List<List<VisualNode>>(maxLayer + 1);
        for (var i = 0; i <= maxLayer; i++) layers.Add([]);
        foreach (var n in nodes)
            layers[depth[n]].Add(n);

        return layers;
    }

    private static void MinimizeCrossings(
        List<List<VisualNode>> layers,
        Dictionary<VisualNode, List<VisualNode>> adj,
        int passes)
    {
        var reverseAdj = new Dictionary<VisualNode, List<VisualNode>>();
        foreach (var (node, _) in adj) reverseAdj[node] = [];
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
        List<VisualNode> layer,
        List<VisualNode> referenceLayer,
        Dictionary<VisualNode, List<VisualNode>> connections)
    {
        var posMap = new Dictionary<VisualNode, int>();
        for (var i = 0; i < referenceLayer.Count; i++)
            posMap[referenceLayer[i]] = i;

        var barycenters = new Dictionary<VisualNode, double>();
        foreach (var node in layer)
        {
            if (!connections.TryGetValue(node, out var neighbors)) continue;
            var positions = new List<int>();
            foreach (var n in neighbors)
                if (posMap.TryGetValue(n, out var pos))
                    positions.Add(pos);

            barycenters[node] = positions.Count > 0
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

    private static void AssignCoordinates(List<List<VisualNode>> layers)
    {
        for (var layerIdx = 0; layerIdx < layers.Count; layerIdx++)
        {
            var layer = layers[layerIdx];
            var totalWidth = (layer.Count - 1) * NodeSpacing;
            var startX = -totalWidth / 2;

            for (var i = 0; i < layer.Count; i++)
            {
                layer[i].X = startX + i * NodeSpacing;
                layer[i].Y = layerIdx * LayerSpacing;
            }
        }
    }

    private static List<List<VisualNode>> FindComponents(
        IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges)
    {
        var adj = new Dictionary<VisualNode, List<VisualNode>>();
        foreach (var n in nodes) adj[n] = [];
        foreach (var e in edges)
        {
            if (adj.ContainsKey(e.Source)) adj[e.Source].Add(e.Target);
            if (adj.ContainsKey(e.Target)) adj[e.Target].Add(e.Source);
        }

        var visited = new HashSet<VisualNode>();
        var components = new List<List<VisualNode>>();

        foreach (var n in nodes)
        {
            if (!visited.Add(n)) continue;
            var comp = new List<VisualNode>();
            var queue = new Queue<VisualNode>();
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

    private static void PackComponents(List<List<VisualNode>> components)
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

    private static void CenterGraph(IReadOnlyList<VisualNode> nodes)
    {
        var cx = 0.0;
        var cy = 0.0;
        foreach (var n in nodes) { cx += n.X; cy += n.Y; }
        cx /= nodes.Count;
        cy /= nodes.Count;
        foreach (var n in nodes) { n.X -= cx; n.Y -= cy; }
    }
}
