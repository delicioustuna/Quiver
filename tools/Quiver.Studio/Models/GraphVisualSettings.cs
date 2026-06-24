namespace Quiver.Studio.Models;

public enum ScoreVizMode { ColorOnly, ColorAndSize, SizeOnly }
public enum ContourMode { Relative, Absolute }
public enum NodeShape { Circle, Square, RoundedRect }
public enum EdgeStyle { Straight, Bezier, Polyline }

public sealed class ColorPalettePreset
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string[] Colors { get; set; }

    public static readonly ColorPalettePreset[] BuiltIn =
    [
        new()
        {
            Name = "Viridis",
            Description = "Perceptually uniform, CVD-safe",
            Colors = ["#440154", "#482777", "#3E4A89", "#31688E", "#26838F",
                       "#1F9D8A", "#6CCE59", "#B6DE2B", "#FDE725"],
        },
        new()
        {
            Name = "Cividis",
            Description = "Optimized for color vision deficiency",
            Colors = ["#002051", "#0D346B", "#3B4F6E", "#5D6B6E", "#80866F",
                       "#A4A06D", "#CBBA5E", "#F0D644", "#FDEA45"],
        },
        new()
        {
            Name = "Inferno",
            Description = "High contrast, perceptually uniform",
            Colors = ["#000004", "#1B0C41", "#4A0C6B", "#781C6D", "#A52C60",
                       "#CF4446", "#ED6925", "#FB9B06", "#F7D13D"],
        },
        new()
        {
            Name = "Blue-Orange",
            Description = "Diverging, deuteranopia-friendly",
            Colors = ["#2166AC", "#4393C3", "#92C5DE", "#D1E5F0", "#F7F7F7",
                       "#FDDBC7", "#F4A582", "#D6604D", "#B2182B"],
        },
    ];
}

public sealed class ContourSettings
{
    public ContourMode Mode { get; set; } = ContourMode.Relative;
    public string PaletteName { get; set; } = "Viridis";
    public string[]? CustomPaletteColors { get; set; }
    public int Steps { get; set; } = 5;
    public double AbsoluteMin { get; set; } = 0.0;
    public double AbsoluteMax { get; set; } = 1.0;
}

public sealed class GraphVisualSettings
{
    public ScoreVizMode ScoreVisualization { get; set; } = ScoreVizMode.ColorAndSize;
    public ContourSettings Contour { get; set; } = new();
    public NodeShape NodeShape { get; set; } = NodeShape.Circle;
    public EdgeStyle EdgeStyle { get; set; } = EdgeStyle.Straight;
}
