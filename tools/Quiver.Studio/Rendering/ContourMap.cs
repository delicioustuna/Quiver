using Avalonia.Media;
using Quiver.Studio.Models;

namespace Quiver.Studio.Rendering;

public sealed class ContourMap
{
    private Color[] _bands = [];
    private double[] _thresholds = [];
    private ContourMode _mode;
    private string _paletteName = "";
    private string[]? _customColors;
    private int _steps;
    private double _absMin;
    private double _absMax;

    public Color[] Bands => _bands;
    public double[] Thresholds => _thresholds;
    public ContourMode Mode => _mode;

    public void Rebuild(ContourSettings settings)
    {
        if (_paletteName == settings.PaletteName
            && _customColors == settings.CustomPaletteColors
            && _steps == settings.Steps
            && _mode == settings.Mode
            && Math.Abs(_absMin - settings.AbsoluteMin) < 1e-9
            && Math.Abs(_absMax - settings.AbsoluteMax) < 1e-9
            && _bands.Length > 0)
            return;

        _paletteName = settings.PaletteName;
        _customColors = settings.CustomPaletteColors;
        _steps = Math.Max(2, settings.Steps);
        _mode = settings.Mode;
        _absMin = settings.AbsoluteMin;
        _absMax = settings.AbsoluteMax;

        var source = ResolveSourceColors(settings);
        _bands = Discretize(source, _steps);
        _thresholds = BuildThresholds(_steps, _mode, _absMin, _absMax);
    }

    public Color Resolve(float rawScore, float dataMin, float dataMax)
    {
        if (_bands.Length == 0) return Colors.Gray;

        double normalized;
        if (_mode == ContourMode.Absolute)
        {
            var range = _absMax - _absMin;
            normalized = range > 0 ? (rawScore - _absMin) / range : 0.5;
        }
        else
        {
            var range = dataMax - dataMin;
            normalized = range > 0 ? (rawScore - dataMin) / range : 0.5;
        }

        normalized = Math.Clamp(normalized, 0.0, 1.0);
        var idx = (int)(normalized * (_bands.Length - 1));
        return _bands[Math.Clamp(idx, 0, _bands.Length - 1)];
    }

    public int BandIndex(float rawScore, float dataMin, float dataMax)
    {
        if (_bands.Length == 0) return 0;

        double normalized;
        if (_mode == ContourMode.Absolute)
        {
            var range = _absMax - _absMin;
            normalized = range > 0 ? (rawScore - _absMin) / range : 0.5;
        }
        else
        {
            var range = dataMax - dataMin;
            normalized = range > 0 ? (rawScore - dataMin) / range : 0.5;
        }

        normalized = Math.Clamp(normalized, 0.0, 1.0);
        var idx = (int)(normalized * (_bands.Length - 1));
        return Math.Clamp(idx, 0, _bands.Length - 1);
    }

    private static double[] BuildThresholds(int steps, ContourMode mode, double absMin, double absMax)
    {
        var t = new double[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var frac = (double)i / steps;
            t[i] = mode == ContourMode.Absolute
                ? absMin + frac * (absMax - absMin)
                : frac;
        }
        return t;
    }

    private static Color[] ResolveSourceColors(ContourSettings s)
    {
        if (s.PaletteName == "Custom" && s.CustomPaletteColors is { Length: >= 2 })
            return ParseColors(s.CustomPaletteColors);

        foreach (var preset in ColorPalettePreset.BuiltIn)
        {
            if (preset.Name == s.PaletteName)
                return ParseColors(preset.Colors);
        }

        return ParseColors(ColorPalettePreset.BuiltIn[0].Colors);
    }

    private static Color[] ParseColors(string[] hexColors)
    {
        var colors = new Color[hexColors.Length];
        for (var i = 0; i < hexColors.Length; i++)
            colors[i] = Color.Parse(hexColors[i]);
        return colors;
    }

    private static Color[] Discretize(Color[] source, int steps)
    {
        if (source.Length == 0) return [Colors.Gray];
        if (steps <= 1) return [source[0]];

        var result = new Color[steps];
        for (var i = 0; i < steps; i++)
        {
            var t = (double)i / (steps - 1);
            var pos = t * (source.Length - 1);
            var lo = (int)Math.Floor(pos);
            var hi = Math.Min(lo + 1, source.Length - 1);
            var frac = pos - lo;

            result[i] = Color.FromArgb(255,
                (byte)(source[lo].R + (source[hi].R - source[lo].R) * frac),
                (byte)(source[lo].G + (source[hi].G - source[lo].G) * frac),
                (byte)(source[lo].B + (source[hi].B - source[lo].B) * frac));
        }
        return result;
    }
}
