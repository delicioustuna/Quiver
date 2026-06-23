using Quiver.Studio.Models;

namespace Quiver.Studio.Rendering;

public static class HitTestHelper
{
    public static VisualNode? HitTestNode(
        IReadOnlyList<VisualNode> nodes,
        CameraTransform camera,
        double screenX,
        double screenY)
    {
        var world = camera.ScreenToWorld(screenX, screenY);
        VisualNode? best = null;
        var bestDist2 = double.MaxValue;

        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            var dx = world.X - n.X;
            var dy = world.Y - n.Y;
            var dist2 = dx * dx + dy * dy;
            var r = n.Radius;
            if (dist2 <= r * r && dist2 < bestDist2)
            {
                best = n;
                bestDist2 = dist2;
            }
        }

        return best;
    }

    private const double EdgeHitTolerance = 5.0;

    public static VisualEdge? HitTestEdge(
        IReadOnlyList<VisualEdge> edges,
        CameraTransform camera,
        double screenX,
        double screenY)
    {
        var world = camera.ScreenToWorld(screenX, screenY);
        var tolerance = EdgeHitTolerance / camera.Zoom;
        VisualEdge? best = null;
        var bestDist = double.MaxValue;

        foreach (var edge in edges)
        {
            var dist = PointToSegmentDistance(
                world.X, world.Y,
                edge.Source.X, edge.Source.Y,
                edge.Target.X, edge.Target.Y);

            if (dist <= tolerance && dist < bestDist)
            {
                best = edge;
                bestDist = dist;
            }
        }

        return best;
    }

    private static double PointToSegmentDistance(
        double px, double py,
        double ax, double ay,
        double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var len2 = dx * dx + dy * dy;

        if (len2 < 1e-12)
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        var projX = ax + t * dx;
        var projY = ay + t * dy;
        return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }
}
