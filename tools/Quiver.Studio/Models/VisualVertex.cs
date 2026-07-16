using Avalonia;
using Avalonia.Media;
using Quiver.Core;

namespace Quiver.Studio.Models;

public sealed class VisualVertex
{
    private static readonly Color[] Palette =
    [
        Color.Parse("#4E79A7"), Color.Parse("#F28E2B"), Color.Parse("#E15759"),
        Color.Parse("#76B7B2"), Color.Parse("#59A14F"), Color.Parse("#EDC948"),
        Color.Parse("#B07AA1"), Color.Parse("#FF9DA7"), Color.Parse("#9C755F"),
        Color.Parse("#BAB0AC"),
    ];

    public VertexId Id { get; }
    public string Label { get; }
    public Color Color { get; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool IsSelected { get; set; }
    public bool IsPinned { get; set; }
    public double Radius { get; set; } = 20;
    public float? VectorScore { get; set; }
    public float? NormalizedScore { get; set; }

    public Point Position
    {
        get => new(X, Y);
        set { X = value.X; Y = value.Y; }
    }

    public VisualVertex(VertexId id, string label)
    {
        Id = id;
        Label = label;
        Color = Palette[(uint)label.GetHashCode() % (uint)Palette.Length];
    }
}
