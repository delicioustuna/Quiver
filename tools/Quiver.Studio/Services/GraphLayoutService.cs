using Quiver.Studio.Models;

namespace Quiver.Studio.Services;

public sealed class GraphLayoutService
{
    private const int BarnesHutThreshold = 500;
    private const double BarnesHutTheta = 0.8;
    private const double IdealEdgeLength = 100.0;
    private const double ComponentGap = 60.0;

    public void Layout(IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges, int iterations = 300)
    {
        if (nodes.Count <= 1)
        {
            if (nodes.Count == 1) { nodes[0].X = 0; nodes[0].Y = 0; }
            return;
        }

        var components = FindComponents(nodes, edges);
        if (components.Count == 1)
        {
            LayoutComponent(nodes, edges, iterations);
            CenterGraph(nodes);
            return;
        }

        foreach (var comp in components)
        {
            var compEdges = edges.Where(e =>
                comp.Contains(e.Source) && comp.Contains(e.Target)).ToList();
            LayoutComponent(comp, compEdges, iterations);
        }

        PackComponents(components);
        CenterGraph(nodes);
    }

    private static List<List<VisualNode>> FindComponents(IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges)
    {
        var adj = new Dictionary<VisualNode, List<VisualNode>>();
        foreach (var n in nodes) adj[n] = [];
        foreach (var e in edges)
        {
            adj[e.Source].Add(e.Target);
            adj[e.Target].Add(e.Source);
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
                {
                    if (visited.Add(neighbor))
                        queue.Enqueue(neighbor);
                }
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

    private void LayoutComponent(IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges, int iterations)
    {
        if (nodes.Count <= 1)
        {
            if (nodes.Count == 1) { nodes[0].X = 0; nodes[0].Y = 0; }
            return;
        }

        var k = IdealEdgeLength;
        var k2 = k * k;

        InitializePositions(nodes, k);

        var useBarnesHut = nodes.Count > BarnesHutThreshold;
        var tMax = k;

        for (var iter = 0; iter < iterations; iter++)
        {
            var t = tMax * (1.0 - (double)iter / iterations);
            if (t < 0.01) break;

            var disp = new double[nodes.Count * 2];

            if (useBarnesHut)
                ApplyRepulsionBarnesHut(nodes, disp, k2);
            else
                ApplyRepulsionNaive(nodes, disp, k2);

            ApplyAttraction(nodes, edges, disp, k);
            ApplyDisplacements(nodes, disp, t);
        }
    }

    private static void InitializePositions(IReadOnlyList<VisualNode> nodes, double k)
    {
        var rng = new Random(42);
        var spread = k * Math.Sqrt(nodes.Count);
        foreach (var node in nodes)
        {
            if (node.IsPinned) continue;
            node.X = (rng.NextDouble() - 0.5) * spread;
            node.Y = (rng.NextDouble() - 0.5) * spread;
        }
    }

    private static void ApplyRepulsionNaive(IReadOnlyList<VisualNode> nodes, double[] disp, double k2)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var dx = nodes[i].X - nodes[j].X;
                var dy = nodes[i].Y - nodes[j].Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 0.01) { dx = 0.1; dy = 0.1; dist = Math.Sqrt(0.02); }

                var force = k2 / dist;
                var fx = dx / dist * force;
                var fy = dy / dist * force;

                disp[i * 2] += fx;
                disp[i * 2 + 1] += fy;
                disp[j * 2] -= fx;
                disp[j * 2 + 1] -= fy;
            }
        }
    }

    private static void ApplyAttraction(
        IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges, double[] disp, double k)
    {
        var indexOf = new Dictionary<VisualNode, int>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
            indexOf[nodes[i]] = i;

        foreach (var edge in edges)
        {
            if (!indexOf.TryGetValue(edge.Source, out var si) ||
                !indexOf.TryGetValue(edge.Target, out var ti))
                continue;

            var dx = edge.Target.X - edge.Source.X;
            var dy = edge.Target.Y - edge.Source.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.01) continue;

            var force = dist * dist / k;
            var fx = dx / dist * force;
            var fy = dy / dist * force;

            disp[si * 2] += fx;
            disp[si * 2 + 1] += fy;
            disp[ti * 2] -= fx;
            disp[ti * 2 + 1] -= fy;
        }
    }

    private static void ApplyDisplacements(IReadOnlyList<VisualNode> nodes, double[] disp, double temperature)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].IsPinned) continue;

            var dx = disp[i * 2];
            var dy = disp[i * 2 + 1];
            var magnitude = Math.Sqrt(dx * dx + dy * dy);
            if (magnitude < 0.01) continue;

            var capped = Math.Min(magnitude, temperature);
            nodes[i].X += dx / magnitude * capped;
            nodes[i].Y += dy / magnitude * capped;
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

    private static void ApplyRepulsionBarnesHut(IReadOnlyList<VisualNode> nodes, double[] disp, double k2)
    {
        var tree = QuadTree.Build(nodes);
        for (var i = 0; i < nodes.Count; i++)
        {
            var (fx, fy) = tree.ComputeForce(nodes[i].X, nodes[i].Y, BarnesHutTheta, k2);
            disp[i * 2] += fx;
            disp[i * 2 + 1] += fy;
        }
    }

    private sealed class QuadTree
    {
        private double _cx, _cy, _mass;
        private double _minX, _minY, _maxX, _maxY;
        private QuadTree?[]? _children;
        private bool _isLeaf;

        public static QuadTree Build(IReadOnlyList<VisualNode> nodes)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var n in nodes)
            {
                if (n.X < minX) minX = n.X;
                if (n.Y < minY) minY = n.Y;
                if (n.X > maxX) maxX = n.X;
                if (n.Y > maxY) maxY = n.Y;
            }

            var pad = Math.Max(maxX - minX, maxY - minY) * 0.01 + 1;
            var root = new QuadTree
            {
                _minX = minX - pad, _minY = minY - pad,
                _maxX = maxX + pad, _maxY = maxY + pad
            };
            foreach (var n in nodes)
                root.Insert(n.X, n.Y);
            return root;
        }

        private void Insert(double x, double y)
        {
            if (_mass == 0)
            {
                _cx = x; _cy = y; _mass = 1; _isLeaf = true;
                return;
            }

            if (_isLeaf)
            {
                _children ??= new QuadTree[4];
                var oldQI = Quadrant(_cx, _cy);
                _children[oldQI] ??= MakeChild(oldQI);
                _children[oldQI]!.Insert(_cx, _cy);
                _isLeaf = false;
            }

            var qi = Quadrant(x, y);
            _children ??= new QuadTree[4];
            _children[qi] ??= MakeChild(qi);
            _children[qi]!.Insert(x, y);

            _cx = (_cx * _mass + x) / (_mass + 1);
            _cy = (_cy * _mass + y) / (_mass + 1);
            _mass++;
        }

        private int Quadrant(double x, double y)
        {
            var midX = (_minX + _maxX) / 2;
            var midY = (_minY + _maxY) / 2;
            return (x > midX ? 1 : 0) + (y > midY ? 2 : 0);
        }

        private QuadTree MakeChild(int qi)
        {
            var midX = (_minX + _maxX) / 2;
            var midY = (_minY + _maxY) / 2;
            return qi switch
            {
                0 => new QuadTree { _minX = _minX, _minY = _minY, _maxX = midX, _maxY = midY },
                1 => new QuadTree { _minX = midX, _minY = _minY, _maxX = _maxX, _maxY = midY },
                2 => new QuadTree { _minX = _minX, _minY = midY, _maxX = midX, _maxY = _maxY },
                _ => new QuadTree { _minX = midX, _minY = midY, _maxX = _maxX, _maxY = _maxY },
            };
        }

        public (double Fx, double Fy) ComputeForce(double px, double py, double theta, double k2)
        {
            if (_mass == 0) return (0, 0);

            var dx = px - _cx;
            var dy = py - _cy;
            var dist2 = dx * dx + dy * dy;
            if (dist2 < 0.01) return (0, 0);

            var size = _maxX - _minX;
            if (_isLeaf || size / Math.Sqrt(dist2) < theta)
            {
                var dist = Math.Sqrt(dist2);
                var force = k2 * _mass / dist;
                return (dx / dist * force, dy / dist * force);
            }

            var fx = 0.0;
            var fy = 0.0;
            if (_children is not null)
            {
                foreach (var child in _children)
                {
                    if (child is null) continue;
                    var (cfx, cfy) = child.ComputeForce(px, py, theta, k2);
                    fx += cfx;
                    fy += cfy;
                }
            }
            return (fx, fy);
        }
    }
}
