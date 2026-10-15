using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TemizlikPaneli.Controls;

/// <summary>0..1 oranını yıldız sütun genişliğine çevirir (çubuk grafiklerin dolu kısmı).</summary>
public sealed class StarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double f = value is double d ? d : 0;
        if (double.IsNaN(f) || f < 0) f = 0;
        if (f > 1) f = 1;
        return new GridLength(f, GridUnitType.Star);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>1 - oran (çubuğun boş kalan kısmı).</summary>
public sealed class InverseStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double f = value is double d ? d : 0;
        if (double.IsNaN(f) || f < 0) f = 0;
        if (f > 1) f = 1;
        return new GridLength(1 - f, GridUnitType.Star);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Yatay çubuk listesindeki bir satır.</summary>
public sealed class BarRow
{
    public string Label { get; init; } = "";
    public string ValueText { get; init; } = "";
    public double Fraction { get; init; }
    public string? Tooltip { get; init; }
    public object? Tag { get; init; }

    /// <summary>En büyük değere göre oranlanmış satırlar üretir.</summary>
    public static List<BarRow> Build<T>(IEnumerable<T> items, Func<T, string> label, Func<T, long> value, Func<long, string> format, long? total = null, Func<T, object?>? tag = null)
    {
        var list = items.Select(i => (Item: i, V: value(i))).Where(x => x.V > 0).OrderByDescending(x => x.V).ToList();
        long max = list.Count > 0 ? list[0].V : 1;
        return list.Select(x => new BarRow
        {
            Label = label(x.Item),
            ValueText = format(x.V),
            Fraction = Math.Max(0.006, (double)x.V / Math.Max(1, max)), // sıfır olmayan değer her zaman görünür kalsın
            Tooltip = total is > 0 ? $"{label(x.Item)}: {format(x.V)} (toplamın {Core.Fmt.Percent((double)x.V / total.Value)})" : $"{label(x.Item)}: {format(x.V)}",
            Tag = tag?.Invoke(x.Item),
        }).ToList();
    }
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x => x,
            string s => s.Length > 0,
            int i => i != 0,
            long l => l != 0,
            null => false,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
