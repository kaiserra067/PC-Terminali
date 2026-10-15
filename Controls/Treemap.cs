using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Controls;

public sealed class TreemapItem
{
    public required string Label { get; init; }
    public long Size { get; init; }
    public bool IsFolder { get; init; }
    public bool IsOther { get; init; }
    public object? Tag { get; init; }
    public List<TreemapItem>? Children { get; init; }
}

/// <summary>
/// Kare oranlı (squarified) ağaç haritası. Klasörler 1. seri rengi, dosyalar 2. seri rengi, küçük öğeler gri "Diğer".
/// Büyük klasörlerin içinde ikinci düzey (alt klasörler) çizilir. Öğeler arasında 2 px yüzey boşluğu bırakılır.
/// </summary>
public sealed class Treemap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IReadOnlyList<TreemapItem>), typeof(Treemap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Treemap)d)._dirty = true));

    public IReadOnlyList<TreemapItem>? Items
    {
        get => (IReadOnlyList<TreemapItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public long Total { get; set; }

    public event Action<TreemapItem>? ItemClicked;

    private readonly record struct Cell(Rect Rect, TreemapItem Item, TreemapItem Top, int Level);
    private readonly List<Cell> _cells = new();
    private readonly HashSet<TreemapItem> _framed = new(ReferenceEqualityComparer.Instance);
    private Size _laidOutFor;
    private bool _dirty = true;
    private TreemapItem? _hoverTop;
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, StaysOpen = true };
    private const int MaxTop = 120, MaxChildren = 40;
    private const double Gap = 2, Header = 20;

    public Treemap()
    {
        ClipToBounds = true;
        _tip.PlacementTarget = this;
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _dirty = true;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ Yerleşim

    private static List<TreemapItem> Prepare(IEnumerable<TreemapItem> items, int max)
    {
        var list = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        if (list.Count <= max) return list;
        var head = list.Take(max - 1).ToList();
        var tail = list.Skip(max - 1).ToList();
        head.Add(new TreemapItem { Label = $"Diğer ({Fmt.Count(tail.Count)} öğe)", Size = tail.Sum(t => t.Size), IsOther = true });
        return head;
    }

    private static List<Rect> Squarify(IReadOnlyList<TreemapItem> items, Rect area)
    {
        var rects = new List<Rect>(items.Count);
        double total = items.Sum(i => (double)i.Size);
        if (total <= 0 || area.Width <= 0 || area.Height <= 0) return rects;
        double scale = area.Width * area.Height / total;
        var vals = items.Select(i => i.Size * scale).ToArray();
        var r = area;
        int start = 0;
        while (start < vals.Length)
        {
            double side = Math.Min(r.Width, r.Height);
            if (side <= 0) { for (int k = start; k < vals.Length; k++) rects.Add(Rect.Empty); break; }
            int end = start;
            double sum = 0, worst = double.MaxValue;
            while (end < vals.Length)
            {
                double ns = sum + vals[end];
                double max = vals[start], min = vals[end];
                double w = Math.Max(side * side * max / (ns * ns), ns * ns / (side * side * min));
                if (end > start && w > worst) break;
                sum = ns; worst = w; end++;
            }
            double thick = sum / side;
            if (r.Width >= r.Height)
            {
                double y = r.Y;
                for (int k = start; k < end; k++) { double h = vals[k] / thick; rects.Add(new Rect(r.X, y, thick, h)); y += h; }
                r = new Rect(r.X + thick, r.Y, Math.Max(0, r.Width - thick), r.Height);
            }
            else
            {
                double x = r.X;
                for (int k = start; k < end; k++) { double w = vals[k] / thick; rects.Add(new Rect(x, r.Y, w, thick)); x += w; }
                r = new Rect(r.X, r.Y + thick, r.Width, Math.Max(0, r.Height - thick));
            }
            start = end;
        }
        return rects;
    }

    private void LayoutCells()
    {
        _cells.Clear();
        _framed.Clear();
        _laidOutFor = RenderSize;
        _dirty = false;
        if (Items == null || Items.Count == 0) return;
        var top = Prepare(Items, MaxTop);
        var rects = Squarify(top, new Rect(RenderSize));
        for (int i = 0; i < top.Count && i < rects.Count; i++)
        {
            var r = Inset(rects[i]);
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
            var item = top[i];
            _cells.Add(new Cell(r, item, item, 1));
            if (item.IsFolder && item.Children is { Count: > 0 } && r.Width >= 72 && r.Height >= 54)
            {
                var inner = new Rect(r.X + 2, r.Y + Header, Math.Max(0, r.Width - 4), Math.Max(0, r.Height - Header - 2));
                var kids = Prepare(item.Children, MaxChildren);
                var kr = Squarify(kids, inner);
                if (kids.Count > 0) _framed.Add(item);
                for (int k = 0; k < kids.Count && k < kr.Count; k++)
                {
                    var cr = Inset(kr[k]);
                    if (cr.IsEmpty || cr.Width < 1 || cr.Height < 1) continue;
                    _cells.Add(new Cell(cr, kids[k], item, 2));
                }
            }
        }
    }

    private static Rect Inset(Rect r) =>
        r.IsEmpty || r.Width <= Gap || r.Height <= Gap ? Rect.Empty : new Rect(r.X + Gap / 2, r.Y + Gap / 2, r.Width - Gap, r.Height - Gap);

    // ------------------------------------------------------------------ Çizim

    protected override void OnRender(DrawingContext dc)
    {
        if (_dirty || _laidOutFor != RenderSize) LayoutCells();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var folder = ThemeManager.Color("Series1");
        var file = ThemeManager.Color("Series2");
        var other = ThemeManager.Color("NeutralMark");
        var ink = ThemeManager.Color("TextPrimary");
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var c in _cells)
        {
            var baseColor = c.Item.IsOther ? other : c.Item.IsFolder ? folder : file;
            bool framed = c.Level == 1 && _framed.Contains(c.Item);
            if (framed)
            {
                var frame = Color.FromArgb(70, baseColor.R, baseColor.G, baseColor.B);
                dc.DrawRoundedRectangle(new SolidColorBrush(frame), null, c.Rect, 4, 4);
                DrawLabel(dc, c.Item, new Rect(c.Rect.X + 6, c.Rect.Y + 2, c.Rect.Width - 12, Header - 2), ink, dip, singleLine: true);
            }
            else
            {
                var fill = c.Level == 2 ? Blend(baseColor, 0.82) : baseColor;
                dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, c.Rect, 3, 3);
                DrawLabel(dc, c.Item, c.Rect, Luminance(fill) > 0.45 ? Color.FromRgb(11, 11, 11) : Colors.White, dip, singleLine: false);
            }
        }

        if (_hoverTop != null)
        {
            var hc = _cells.FirstOrDefault(x => x.Level == 1 && ReferenceEquals(x.Item, _hoverTop));
            if (hc.Item != null)
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(ink), 2), new Rect(hc.Rect.X + 1, hc.Rect.Y + 1, Math.Max(0, hc.Rect.Width - 2), Math.Max(0, hc.Rect.Height - 2)), 4, 4);
        }
    }

    private static Color Blend(Color c, double keep) =>
        Color.FromArgb(255, (byte)(c.R * keep), (byte)(c.G * keep), (byte)(c.B * keep));

    private static double Luminance(Color c)
    {
        static double Ch(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface FaceNormal = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Etiket yalnızca sığıyorsa çizilir; asla kırpılmaz.</summary>
    private static void DrawLabel(DrawingContext dc, TreemapItem item, Rect r, Color color, double dip, bool singleLine)
    {
        if (r.Width < 40 || r.Height < 16) return;
        var brush = new SolidColorBrush(color);
        var name = new FormattedText(item.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 12, brush, dip)
        { MaxTextWidth = Math.Max(1, r.Width - 12), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (singleLine)
        {
            var label = new FormattedText($"{item.Label}  ·  {Fmt.Size(item.Size)}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11.5, brush, dip)
            { MaxTextWidth = Math.Max(1, r.Width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            if (r.Width >= 60) dc.DrawText(label, new Point(r.X, r.Y + 1));
            return;
        }
        if (r.Height < 22 || r.Width < 56) return;
        dc.DrawText(name, new Point(r.X + 6, r.Y + 4));
        if (r.Height >= 40)
        {
            var size = new FormattedText(Fmt.Size(item.Size), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, FaceNormal, 11.5,
                new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)), dip) { MaxTextWidth = Math.Max(1, r.Width - 12), MaxLineCount = 1 };
            dc.DrawText(size, new Point(r.X + 6, r.Y + 21));
        }
    }

    // ------------------------------------------------------------------ Etkileşim

    private Cell? HitTest(Point p)
    {
        Cell? best = null;
        foreach (var c in _cells)
            if (c.Rect.Contains(p) && (best == null || c.Level > best.Value.Level)) best = c;
        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        var top = hit?.Top;
        if (!ReferenceEquals(top, _hoverTop)) { _hoverTop = top; InvalidateVisual(); }
        Cursor = top is { IsFolder: true } ? Cursors.Hand : Cursors.Arrow;
        if (hit is { } h)
        {
            var parts = new List<string> { $"{h.Item.Label} — {Fmt.Size(h.Item.Size)}" };
            if (Total > 0) parts[0] += $" ({Fmt.Percent((double)h.Item.Size / Total)})";
            if (h.Level == 2) parts.Add($"{h.Top.Label} içinde");
            parts.Add(h.Item.IsOther ? "Küçük öğelerin toplamı" : h.Top.IsFolder ? "Açmak için tıklayın" : "Dosya");
            _tip.Content = string.Join("\n", parts);
            _tip.HorizontalOffset = p.X + 16;
            _tip.VerticalOffset = p.Y + 18;
            _tip.IsOpen = true;
        }
        else _tip.IsOpen = false;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.IsOpen = false;
        if (_hoverTop != null) { _hoverTop = null; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } h && !h.Top.IsOther)
        {
            _tip.IsOpen = false;
            ItemClicked?.Invoke(h.Top);
        }
    }
}
