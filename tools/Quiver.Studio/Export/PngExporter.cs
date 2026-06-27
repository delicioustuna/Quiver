using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Quiver.Studio.Models;
using Quiver.Studio.Rendering;

namespace Quiver.Studio.Export;

public static class PngExporter
{
    public static void Export(
        IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<VisualEdge> edges,
        bool isDarkTheme,
        GraphVisualSettings visualSettings,
        string outputPath,
        double scale = 2.0)
    {
        if (nodes.Count == 0) return;

        ComputeBounds(nodes, out var minX, out var minY, out var maxX, out var maxY);
        const double margin = 40;
        var width = (maxX - minX) + margin * 2;
        var height = (maxY - minY) + margin * 2;

        var pixelW = (int)(width * scale);
        var pixelH = (int)(height * scale);
        if (pixelW <= 0 || pixelH <= 0) return;

        var renderer = new GraphRenderer
        {
            IsDarkTheme = isDarkTheme,
            VisualSettings = visualSettings,
        };
        renderer.Camera.OffsetX = (-minX + margin) * scale;
        renderer.Camera.OffsetY = (-minY + margin) * scale;
        renderer.Camera.Zoom = scale;

        using var bitmap = new RenderTargetBitmap(new PixelSize(pixelW, pixelH));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var bg = isDarkTheme ? Color.Parse("#1e1e1e") : Colors.White;
            ctx.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, pixelW, pixelH));
            renderer.Render(ctx, nodes, edges);
        }
        bitmap.Save(outputPath);
    }

    private static void ComputeBounds(
        IReadOnlyList<VisualNode> nodes,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.MaxValue; minY = double.MaxValue;
        maxX = double.MinValue; maxY = double.MinValue;
        foreach (var n in nodes)
        {
            var r = n.Radius;
            if (n.X - r < minX) minX = n.X - r;
            if (n.Y - r < minY) minY = n.Y - r;
            if (n.X + r > maxX) maxX = n.X + r;
            if (n.Y + r + 20 > maxY) maxY = n.Y + r + 20;
        }
    }
}
