using System.Globalization;
using System.Text;
using Quiver.Studio.Models;

namespace Quiver.Studio.Export;

public static class SvgExporter
{
    private const double Margin = 40;
    private const double ArrowSize = 8;

    public static string Export(
        IReadOnlyList<VisualVertex> vertices,
        IReadOnlyList<VisualEdge> edges,
        bool isDarkTheme)
    {
        if (vertices.Count == 0)
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"/>";

        ComputeBounds(vertices, out var minX, out var minY, out var maxX, out var maxY);
        var vbX = minX - Margin;
        var vbY = minY - Margin;
        var vbW = (maxX - minX) + Margin * 2;
        var vbH = (maxY - minY) + Margin * 2;

        var bg = isDarkTheme ? "#1e1e1e" : "#ffffff";
        var labelFill = isDarkTheme ? "#ffffff" : "#000000";
        var edgeLabelFill = isDarkTheme ? "#aaaaaa" : "#696969";

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" ");
        sb.AppendLine(F($"viewBox=\"{vbX} {vbY} {vbW} {vbH}\">"));

        sb.AppendLine("<defs>");
        sb.AppendLine("  <marker id=\"arrowhead\" markerWidth=\"10\" markerHeight=\"7\" refX=\"10\" refY=\"3.5\" orient=\"auto\">");
        sb.AppendLine($"    <polygon points=\"0 0, 10 3.5, 0 7\" fill=\"gray\"/>");
        sb.AppendLine("  </marker>");
        sb.AppendLine("</defs>");

        sb.AppendLine(F($"<rect x=\"{vbX}\" y=\"{vbY}\" width=\"{vbW}\" height=\"{vbH}\" fill=\"{bg}\"/>"));

        foreach (var edge in edges)
            EmitEdge(sb, edge, edgeLabelFill);

        foreach (var vertex in vertices)
            EmitVertex(sb, vertex, labelFill);

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void EmitEdge(StringBuilder sb, VisualEdge edge, string labelFill)
    {
        var sx = edge.Source.X;
        var sy = edge.Source.Y;
        var tx = edge.Target.X;
        var ty = edge.Target.Y;

        var dx = tx - sx;
        var dy = ty - sy;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;

        var nx = dx / len;
        var ny = dy / len;

        var x1 = sx + nx * edge.Source.Radius;
        var y1 = sy + ny * edge.Source.Radius;
        var x2 = tx - nx * (edge.Target.Radius + ArrowSize);
        var y2 = ty - ny * (edge.Target.Radius + ArrowSize);

        sb.AppendLine(F($"<line x1=\"{x1}\" y1=\"{y1}\" x2=\"{x2}\" y2=\"{y2}\" stroke=\"gray\" stroke-width=\"1.5\" marker-end=\"url(#arrowhead)\"/>"));

        if (!string.IsNullOrEmpty(edge.EdgeType))
        {
            var mx = (x1 + x2) / 2;
            var my = (y1 + y2) / 2;
            sb.AppendLine(F($"<text x=\"{mx}\" y=\"{my - 4}\" text-anchor=\"middle\" font-size=\"10\" font-family=\"Inter,sans-serif\" fill=\"{labelFill}\">{Esc(edge.EdgeType)}</text>"));
        }
    }

    private static void EmitVertex(StringBuilder sb, VisualVertex vertex, string labelFill)
    {
        var cx = vertex.X;
        var cy = vertex.Y;
        var r = vertex.Radius;
        var fill = $"#{vertex.Color.R:X2}{vertex.Color.G:X2}{vertex.Color.B:X2}";

        sb.AppendLine(F($"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" fill=\"{fill}\"/>"));

        var label = Esc(vertex.Label);
        sb.AppendLine(F($"<text x=\"{cx}\" y=\"{cy + r + 14}\" text-anchor=\"middle\" font-size=\"11\" font-family=\"Inter,sans-serif\" fill=\"{labelFill}\">{label}</text>"));
    }

    private static void ComputeBounds(
        IReadOnlyList<VisualVertex> vertices,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.MaxValue; minY = double.MaxValue;
        maxX = double.MinValue; maxY = double.MinValue;
        foreach (var n in vertices)
        {
            var r = n.Radius;
            if (n.X - r < minX) minX = n.X - r;
            if (n.Y - r < minY) minY = n.Y - r;
            if (n.X + r > maxX) maxX = n.X + r;
            if (n.Y + r + 20 > maxY) maxY = n.Y + r + 20;
        }
    }

    private static string F(FormattableString s)
        => s.ToString(CultureInfo.InvariantCulture);

    private static string Esc(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
