using Avalonia;
using Avalonia.Media;
using Quiver.Studio.Models;

namespace Quiver.Studio.Rendering;

public sealed class GraphRenderer
{
    private static readonly IPen SelectedPen = new Pen(Brushes.DodgerBlue, 3);
    private static readonly Typeface LabelTypeface = new("Inter", FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface EdgeTypeface = new("Inter", FontStyle.Normal, FontWeight.Light);

    private const double ArrowSize = 8;
    private const double RoundedRectCornerRadius = 6;

    public CameraTransform Camera { get; } = new();
    public bool IsDarkTheme { get; set; }
    public GraphVisualSettings VisualSettings { get; set; } = new();
    public ContourMap Contour { get; } = new();

    public void Render(DrawingContext ctx, IReadOnlyList<VisualVertex> vertices, IReadOnlyList<VisualEdge> edges)
    {
        Contour.Rebuild(VisualSettings.Contour);

        foreach (var edge in edges)
            DrawEdge(ctx, edge);

        foreach (var vertex in vertices)
            DrawVertex(ctx, vertex, vertices);
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

        var pen = edge.IsSelected ? SelectedPen : new Pen(Brushes.Gray, 1.5);

        switch (VisualSettings.EdgeStyle)
        {
            case EdgeStyle.Bezier:
                DrawBezierEdge(ctx, p1, p2, nx, ny, pen);
                break;
            case EdgeStyle.Polyline:
                DrawPolylineEdge(ctx, p1, p2, nx, ny, pen);
                break;
            default:
                ctx.DrawLine(pen, p1, p2);
                break;
        }

        DrawArrowhead(ctx, p2, nx, ny, edge.IsSelected);
        DrawEdgeLabel(ctx, edge, p1, p2);
    }

    private static void DrawBezierEdge(DrawingContext ctx, Point p1, Point p2, double nx, double ny, IPen pen)
    {
        var midX = (p1.X + p2.X) / 2;
        var midY = (p1.Y + p2.Y) / 2;
        var offset = Math.Min(40, Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y)) * 0.2);
        var ctrlX = midX - ny * offset;
        var ctrlY = midY + nx * offset;

        var geometry = new StreamGeometry();
        using (var sgCtx = geometry.Open())
        {
            sgCtx.BeginFigure(p1, false);
            sgCtx.QuadraticBezierTo(new Point(ctrlX, ctrlY), p2);
            sgCtx.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geometry);
    }

    private static void DrawPolylineEdge(DrawingContext ctx, Point p1, Point p2, double nx, double ny, IPen pen)
    {
        var midX = (p1.X + p2.X) / 2;
        var midY = (p1.Y + p2.Y) / 2;
        var offset = 12.0;
        var corner = new Point(midX - ny * offset, midY + nx * offset);

        ctx.DrawLine(pen, p1, corner);
        ctx.DrawLine(pen, corner, p2);
    }

    private void DrawArrowhead(DrawingContext ctx, Point tip, double nx, double ny, bool isSelected)
    {
        var arrowLen = ArrowSize * Camera.Zoom;
        var arrowBase = new Point(tip.X - nx * arrowLen, tip.Y - ny * arrowLen);
        var perpX = -ny * arrowLen * 0.5;
        var perpY = nx * arrowLen * 0.5;

        var geometry = new StreamGeometry();
        using (var sgCtx = geometry.Open())
        {
            sgCtx.BeginFigure(tip, true);
            sgCtx.LineTo(new Point(arrowBase.X + perpX, arrowBase.Y + perpY));
            sgCtx.LineTo(new Point(arrowBase.X - perpX, arrowBase.Y - perpY));
            sgCtx.EndFigure(true);
        }
        ctx.DrawGeometry(isSelected ? Brushes.DodgerBlue : Brushes.Gray, null, geometry);
    }

    private void DrawEdgeLabel(DrawingContext ctx, VisualEdge edge, Point p1, Point p2)
    {
        if (string.IsNullOrEmpty(edge.EdgeType)) return;

        var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
        var fontSize = Math.Max(9, 10 * Camera.Zoom);
        var text = new FormattedText(
            edge.EdgeType,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            EdgeTypeface,
            fontSize,
            Brushes.DimGray);
        ctx.DrawText(text, new Point(mid.X - text.Width / 2, mid.Y - text.Height - 2));
    }

    private void DrawVertex(DrawingContext ctx, VisualVertex vertex, IReadOnlyList<VisualVertex> allVertices)
    {
        var center = Camera.WorldToScreen(vertex.X, vertex.Y);
        var baseR = vertex.Radius * Camera.Zoom;
        var r = baseR;

        var mode = VisualSettings.ScoreVisualization;
        var color = vertex.Color;

        if (vertex.VectorScore.HasValue)
        {
            ComputeDataRange(allVertices, out var dataMin, out var dataMax);

            if (mode is ScoreVizMode.ColorOnly or ScoreVizMode.ColorAndSize)
                color = Contour.Resolve(vertex.VectorScore.Value, dataMin, dataMax);

            if (mode is ScoreVizMode.SizeOnly or ScoreVizMode.ColorAndSize)
            {
                double normalized;
                if (VisualSettings.Contour.Mode == ContourMode.Absolute)
                {
                    var range = VisualSettings.Contour.AbsoluteMax - VisualSettings.Contour.AbsoluteMin;
                    normalized = range > 0 ? (vertex.VectorScore.Value - VisualSettings.Contour.AbsoluteMin) / range : 0.5;
                }
                else
                {
                    var range = dataMax - dataMin;
                    normalized = range > 0 ? (vertex.VectorScore.Value - dataMin) / range : 0.5;
                }
                normalized = Math.Clamp(normalized, 0.0, 1.0);
                r = baseR * (0.7 + 0.6 * normalized);
            }
        }

        var brush = new SolidColorBrush(color);
        var pen = vertex.IsSelected ? SelectedPen : null;

        switch (VisualSettings.VertexShape)
        {
            case VertexShape.Square:
                ctx.DrawRectangle(brush, pen, new Rect(center.X - r, center.Y - r, r * 2, r * 2));
                break;
            case VertexShape.RoundedRect:
                ctx.DrawRectangle(brush, pen,
                    new Rect(center.X - r, center.Y - r, r * 2, r * 2),
                    RoundedRectCornerRadius, RoundedRectCornerRadius);
                break;
            default:
                ctx.DrawEllipse(brush, pen, center, r, r);
                break;
        }

        DrawVertexLabel(ctx, vertex, center, r);
        DrawScoreBadge(ctx, vertex, center, r);
    }

    private static void ComputeDataRange(IReadOnlyList<VisualVertex> vertices, out float min, out float max)
    {
        min = float.MaxValue;
        max = float.MinValue;
        foreach (var n in vertices)
        {
            if (!n.VectorScore.HasValue) continue;
            var s = n.VectorScore.Value;
            if (s < min) min = s;
            if (s > max) max = s;
        }
        if (min > max) { min = 0; max = 1; }
    }

    private void DrawVertexLabel(DrawingContext ctx, VisualVertex vertex, Point center, double r)
    {
        var fontSize = Math.Max(9, 11 * Camera.Zoom);
        var innerText = new FormattedText(
            vertex.Label,
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
                vertex.Label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                fontSize,
                outerBrush);
            ctx.DrawText(outerText, new Point(center.X - outerText.Width / 2, center.Y + r + 2));
        }
    }

    private void DrawScoreBadge(DrawingContext ctx, VisualVertex vertex, Point center, double r)
    {
        if (!vertex.VectorScore.HasValue) return;

        var fontSize = Math.Max(9, 11 * Camera.Zoom);
        var badgeFontSize = Math.Max(7, 9 * Camera.Zoom);
        var scoreText = new FormattedText(
            vertex.VectorScore.Value.ToString("F2"),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            badgeFontSize,
            IsDarkTheme ? Brushes.LightGray : Brushes.DimGray);
        ctx.DrawText(scoreText, new Point(center.X - scoreText.Width / 2, center.Y + r + fontSize + 2));
    }
}
