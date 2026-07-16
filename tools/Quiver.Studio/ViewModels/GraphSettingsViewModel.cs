using CommunityToolkit.Mvvm.ComponentModel;
using Quiver.Studio.Models;
using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public sealed partial class GraphSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly GraphCanvasViewModel _canvas;
    private bool _loading;

    // --- スコアの可視化 ---
    [ObservableProperty] private int _scoreVizModeIndex;

    // --- 輪郭 ---
    [ObservableProperty] private int _contourModeIndex;
    [ObservableProperty] private int _paletteModeIndex;
    [ObservableProperty] private int _paletteSteps;
    [ObservableProperty] private string _customColors = "";
    [ObservableProperty] private double _absoluteMin;
    [ObservableProperty] private double _absoluteMax = 1.0;

    // --- 形状 ---
    [ObservableProperty] private int _vertexShapeIndex;
    [ObservableProperty] private int _edgeStyleIndex;

    // --- 言語 ---
    [ObservableProperty] private int _languageIndex;

    // --- プレビューのバインディング ---
    [ObservableProperty] private ContourSettings? _contourPreview;

    public string[] ScoreVizModes { get; } = ["Color only", "Color + Size", "Size only"];
    public string[] ContourModes { get; } = ["Relative (auto min-max)", "Absolute (fixed range)"];
    public string[] PaletteNames { get; }
    public string[] PaletteDescriptions { get; }
    public string[] VertexShapes { get; } = ["Circle", "Square", "Rounded Rect"];
    public string[] EdgeStyles { get; } = ["Straight", "Bezier", "Polyline"];
    public string[] Languages { get; } = ["Auto", "English", "日本語"];

    public bool IsAbsoluteMode => ContourModeIndex == 1;

    public GraphSettingsViewModel(SettingsService settings, GraphCanvasViewModel canvas)
    {
        _settings = settings;
        _canvas = canvas;

        var names = new List<string>();
        var descs = new List<string>();
        foreach (var p in ColorPalettePreset.BuiltIn)
        {
            names.Add(p.Name);
            descs.Add(p.Description);
        }
        names.Add("Custom");
        descs.Add("User-defined hex colors");
        PaletteNames = names.ToArray();
        PaletteDescriptions = descs.ToArray();

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var gs = _settings.Settings.GraphVisual;
        var c = gs.Contour;

        ScoreVizModeIndex = (int)gs.ScoreVisualization;
        ContourModeIndex = (int)c.Mode;
        PaletteSteps = c.Steps;
        AbsoluteMin = c.AbsoluteMin;
        AbsoluteMax = c.AbsoluteMax;
        VertexShapeIndex = (int)gs.VertexShape;
        EdgeStyleIndex = (int)gs.EdgeStyle;
        CustomColors = c.CustomPaletteColors is not null
            ? string.Join(", ", c.CustomPaletteColors)
            : "";

        var idx = Array.IndexOf(PaletteNames, c.PaletteName);
        PaletteModeIndex = idx >= 0 ? idx : 0;

        LanguageIndex = _settings.Settings.Language switch
        {
            "en" => 1,
            "ja" => 2,
            _ => 0,
        };

        _loading = false;
        RefreshPreview();
    }

    partial void OnScoreVizModeIndexChanged(int value) => Apply();

    partial void OnContourModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAbsoluteMode));
        Apply();
    }

    partial void OnPaletteModeIndexChanged(int value) => Apply();
    partial void OnPaletteStepsChanged(int value) => Apply();
    partial void OnCustomColorsChanged(string value) => Apply();
    partial void OnAbsoluteMinChanged(double value) => Apply();
    partial void OnAbsoluteMaxChanged(double value) => Apply();
    partial void OnVertexShapeIndexChanged(int value) => Apply();
    partial void OnEdgeStyleIndexChanged(int value) => Apply();

    partial void OnLanguageIndexChanged(int value)
    {
        if (_loading) return;
        _settings.Settings.Language = value switch
        {
            1 => "en",
            2 => "ja",
            _ => "auto",
        };
        _settings.MarkDirty();
    }

    private void Apply()
    {
        if (_loading) return;

        var gs = _settings.Settings.GraphVisual;
        gs.ScoreVisualization = (ScoreVizMode)ScoreVizModeIndex;
        gs.VertexShape = (VertexShape)VertexShapeIndex;
        gs.EdgeStyle = (EdgeStyle)EdgeStyleIndex;

        var c = gs.Contour;
        c.Mode = (ContourMode)ContourModeIndex;
        c.PaletteName = PaletteNames[PaletteModeIndex];
        c.Steps = Math.Max(2, PaletteSteps);
        c.AbsoluteMin = AbsoluteMin;
        c.AbsoluteMax = AbsoluteMax;

        if (c.PaletteName == "Custom" && !string.IsNullOrWhiteSpace(CustomColors))
        {
            c.CustomPaletteColors = CustomColors
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.StartsWith('#') && s.Length is 7 or 9)
                .ToArray();
        }

        _settings.MarkDirty();
        _canvas.ApplyVisualSettings(gs);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var c = _settings.Settings.GraphVisual.Contour;
        ContourPreview = new ContourSettings
        {
            Mode = c.Mode,
            PaletteName = c.PaletteName,
            CustomPaletteColors = c.CustomPaletteColors,
            Steps = c.Steps,
            AbsoluteMin = c.AbsoluteMin,
            AbsoluteMax = c.AbsoluteMax,
        };
    }
}
