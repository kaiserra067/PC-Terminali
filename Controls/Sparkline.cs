using System.Windows;
using System.Windows.Media;

namespace TemizlikPaneli.Controls;

/// <summary>Son N ölçümün tek serili çizgi grafiği (2 px çizgi, %10 dolgu, ince taban çizgisi).</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly List<double> _values = new();
    public int Capacity { get; set; } = 60;
    public double Maximum { get; set; } = 100;
    public string BrushKey { get; set; } = "Series1";

    public Sparkline()
    {
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    public void Push(double v)
    {
        _values.Add(Math.Clamp(v, 0, Maximum));
        if (_values.Count > Capacity) _values.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var grid = new Pen(ThemeManager.Brush("BorderBrush"), 1);
        dc.DrawLine(grid, new Point(0, h - 0.5), new Point(w, h - 0.5));
        dc.DrawLine(grid, new Point(0, 0.5), new Point(w, 0.5));
        if (_values.Count < 2) return;

        var color = ThemeManager.Color(BrushKey);
        double step = w / (Capacity - 1);
        double x0 = w - (_values.Count - 1) * step;
        Point P(int i) => new(x0 + i * step, h - 1 - _values[i] / Maximum * (h - 2));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (int i = 1; i < _values.Count; i++) g.LineTo(P(i), true, true);
        }
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < _values.Count; i++) g.LineTo(P(i), true, true);
            g.LineTo(new Point(w, h), true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B)), null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        var last = P(_values.Count - 1);
        dc.DrawEllipse(ThemeManager.Brush("SurfaceBg"), null, last, 5, 5);
        dc.DrawEllipse(new SolidColorBrush(color), null, last, 3.5, 3.5);
    }
}
