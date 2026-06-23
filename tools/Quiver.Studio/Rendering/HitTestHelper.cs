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
}
