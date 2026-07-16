using Avalonia;

namespace Quiver.Studio.Rendering;

public sealed class CameraTransform
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 10.0;

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = 1.0;

    public Point WorldToScreen(double wx, double wy)
        => new(wx * Zoom + OffsetX, wy * Zoom + OffsetY);

    public Point ScreenToWorld(double sx, double sy)
        => new((sx - OffsetX) / Zoom, (sy - OffsetY) / Zoom);

    public void Pan(double dx, double dy)
    {
        OffsetX += dx;
        OffsetY += dy;
    }

    public void ZoomAt(double factor, double screenX, double screenY)
    {
        var newZoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        var worldBefore = ScreenToWorld(screenX, screenY);
        Zoom = newZoom;
        var worldAfter = ScreenToWorld(screenX, screenY);
        OffsetX += (worldAfter.X - worldBefore.X) * Zoom;
        OffsetY += (worldAfter.Y - worldBefore.Y) * Zoom;
    }

    public void FitToContent(IReadOnlyList<Models.VisualVertex> vertices, double viewWidth, double viewHeight)
    {
        if (vertices.Count == 0) return;

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var n in vertices)
        {
            var r = n.Radius;
            if (n.X - r < minX) minX = n.X - r;
            if (n.Y - r < minY) minY = n.Y - r;
            if (n.X + r > maxX) maxX = n.X + r;
            if (n.Y + r > maxY) maxY = n.Y + r;
        }

        var graphWidth = maxX - minX;
        var graphHeight = maxY - minY;
        if (graphWidth < 1) graphWidth = 1;
        if (graphHeight < 1) graphHeight = 1;

        var margin = 40.0;
        var scaleX = (viewWidth - margin * 2) / graphWidth;
        var scaleY = (viewHeight - margin * 2) / graphHeight;
        Zoom = Math.Clamp(Math.Min(scaleX, scaleY), MinZoom, MaxZoom);

        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        OffsetX = viewWidth / 2 - centerX * Zoom;
        OffsetY = viewHeight / 2 - centerY * Zoom;
    }
}
