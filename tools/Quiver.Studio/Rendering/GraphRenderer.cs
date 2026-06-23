using Avalonia;
using Avalonia.Media;
using Quiver.Studio.Models;

namespace Quiver.Studio.Rendering;

public sealed class GraphRenderer
{
    private static readonly IPen EdgePen = new Pen(Brushes.Gray, 1.5);
    private static readonly IPen SelectedPen = new Pen(Brushes.DodgerBlue, 3);
    private static readonly Typeface LabelTypeface = new("Inter", FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface EdgeTypeface = new("Inter", FontStyle.Normal, FontWeight.Light);

    private const double ArrowSize = 8;

    public CameraTransform Camera { get; } = new();
    public bool IsDarkTheme { get; set; }

    public void Render(DrawingContext ctx, IReadOnlyList<VisualNode> nodes, IReadOnlyList<VisualEdge> edges)
    {
        foreach (var edge in edges)
            DrawEdge(ctx, edge);

        foreach (var node in nodes)
            DrawNode(ctx, node);
    }

    private void DrawEdge(DrawingContext ctx, VisualEdge edge)
    {
        var s = Camera.WorldToScreen(edge.Source.X, edge.Source.Y);
        var t = Camera.WorldToScreen(edge.Target.X, edge.Target.Y);

        var dx = t.X - s.X;
        var dy = t.Y - s.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;

        var nx = dx / len;
        var ny = dy / len;

        var sourceR = edge.Source.Radius * Camera.Zoom;
        var targetR = edge.Target.Radius * Camera.Zoom;
        var p1 = new Point(s.X + nx * sourceR, s.Y + ny * sourceR);
        var p2 = new Point(t.X - nx * targetR, t.Y - ny * targetR);

        var pen = edge.IsSelected ? SelectedPen : EdgePen;
        ctx.DrawLine(pen, p1, p2);

        var arrowLen = ArrowSize * Camera.Zoom;
        var arrowBase = new Point(p2.X - nx * arrowLen, p2.Y - ny * arrowLen);
        var perpX = -ny * arrowLen * 0.5;
        var perpY = nx * arrowLen * 0.5;

        var geometry = new StreamGeometry();
        using (var sgCtx = geometry.Open())
        {
            sgCtx.BeginFigure(p2, true);
            sgCtx.LineTo(new Point(arrowBase.X + perpX, arrowBase.Y + perpY));
            sgCtx.LineTo(new Point(arrowBase.X - perpX, arrowBase.Y - perpY));
            sgCtx.EndFigure(true);
        }
        var arrowBrush = edge.IsSelected ? Brushes.DodgerBlue : Brushes.Gray;
        ctx.DrawGeometry(arrowBrush, null, geometry);

        if (!string.IsNullOrEmpty(edge.RelationshipType))
        {
            var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            var fontSize = Math.Max(9, 10 * Camera.Zoom);
            var text = new FormattedText(
                edge.RelationshipType,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                EdgeTypeface,
                fontSize,
                Brushes.DimGray);
            ctx.DrawText(text, new Point(mid.X - text.Width / 2, mid.Y - text.Height - 2));
        }
    }

    private void DrawNode(DrawingContext ctx, VisualNode node)
    {
        var center = Camera.WorldToScreen(node.X, node.Y);
        var r = node.Radius * Camera.Zoom;

        var brush = new SolidColorBrush(node.Color);
        ctx.DrawEllipse(brush, node.IsSelected ? SelectedPen : null, center, r, r);

        var fontSize = Math.Max(9, 11 * Camera.Zoom);

        var innerText = new FormattedText(
            node.Label,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            fontSize,
            Brushes.White);

        if (innerText.Width < r * 2 - 4)
        {
            ctx.DrawText(innerText, new Point(center.X - innerText.Width / 2, center.Y - innerText.Height / 2));
        }
        else
        {
            var outerBrush = IsDarkTheme ? Brushes.White : Brushes.Black;
            var outerText = new FormattedText(
                node.Label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                fontSize,
                outerBrush);
            ctx.DrawText(outerText, new Point(center.X - outerText.Width / 2, center.Y + r + 2));
        }
    }
}
