using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Quiver.Studio.Models;
using Quiver.Studio.Rendering;

namespace Quiver.Studio.Views;

public sealed class ContourPreview : Control
{
    public static readonly StyledProperty<ContourSettings?> ContourProperty =
        AvaloniaProperty.Register<ContourPreview, ContourSettings?>(nameof(Contour));

    public ContourSettings? Contour
    {
        get => GetValue(ContourProperty);
        set => SetValue(ContourProperty, value);
    }

    private static readonly Typeface LabelTypeface = new("Inter", FontStyle.Normal, FontWeight.Normal);
    private readonly ContourMap _map = new();

    static ContourPreview()
    {
        AffectsRender<ContourPreview>(ContourProperty);
    }

    public ContourPreview()
    {
        MinHeight = 52;
    }

    public override void Render(DrawingContext ctx)
    {
        var cs = Contour;
        if (cs is null) return;

        _map.Rebuild(cs);
        var bands = _map.Bands;
        var thresholds = _map.Thresholds;
        if (bands.Length == 0) return;

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var labelBrush = isDark ? Brushes.LightGray : Brushes.DimGray;
        var borderPen = new Pen(isDark ? Brushes.Gray : Brushes.DarkGray, 0.5);

        var barHeight = 24.0;
        var labelY = barHeight + 4;
        var w = Bounds.Width;
        var bandWidth = w / bands.Length;

        for (var i = 0; i < bands.Length; i++)
        {
            var x = i * bandWidth;
            var rect = new Rect(x, 0, bandWidth + 0.5, barHeight);
            ctx.DrawRectangle(new SolidColorBrush(bands[i]), borderPen, rect);
        }

        ctx.DrawRectangle(null, borderPen, new Rect(0, 0, w, barHeight));

        var fontSize = 9.0;
        for (var i = 0; i <= bands.Length; i++)
        {
            if (i >= thresholds.Length) break;

            var label = cs.Mode == ContourMode.Absolute
                ? thresholds[i].ToString("F2")
                : (thresholds[i] * 100).ToString("F0") + "%";

            var text = new FormattedText(label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface, fontSize, labelBrush);

            var x = i * bandWidth - text.Width / 2;
            x = Math.Clamp(x, 0, Math.Max(0, w - text.Width));
            ctx.DrawText(text, new Point(x, labelY));
        }
    }
}
