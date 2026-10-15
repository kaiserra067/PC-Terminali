using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TemizlikPaneli.Views;

/// <summary>Temaya uygun onay / bilgi pencereleri.</summary>
public static class Dialogs
{
    public enum Kind { Info, Warning, Danger, Success, Error }

    public static bool Confirm(string title, string message, string okText = "Devam", bool danger = false, IEnumerable<string>? details = null)
        => Show(title, message, danger ? Kind.Danger : Kind.Warning, okText, "Vazgeç", details) == true;

    public static void Info(string title, string message, IEnumerable<string>? details = null)
        => Show(title, message, Kind.Info, "Tamam", null, details);

    public static void Success(string title, string message, IEnumerable<string>? details = null)
        => Show(title, message, Kind.Success, "Tamam", null, details);

    public static void Error(string title, string message, IEnumerable<string>? details = null)
        => Show(title, message, Kind.Error, "Tamam", null, details);

    public static bool? Show(string title, string message, Kind kind, string okText, string? cancelText, IEnumerable<string>? details = null)
    {
        var owner = Application.Current?.MainWindow;
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = 520,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null || !owner.IsVisible,
            WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = ThemeManager.Brush("SurfaceBg"),
            Foreground = ThemeManager.Brush("TextPrimary"),
            FontFamily = (FontFamily)Application.Current!.FindResource("UiFont"),
            FontSize = 13,
        };
        if (owner != null && owner.IsVisible) win.Owner = owner;
        win.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(win);

        var (glyph, brushKey) = kind switch
        {
            Kind.Danger => ("", "CriticalBrush"),
            Kind.Error => ("", "CriticalBrush"),
            Kind.Warning => ("", "WarningBrush"),
            Kind.Success => ("", "GoodBrush"),
            _ => ("", "AccentHoverBrush"),
        };

        var root = new Grid { Margin = new Thickness(24, 22, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 20,
            Foreground = ThemeManager.Brush(brushKey), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        });
        head.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 430 });
        root.Children.Add(head);

        var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("TextSecondary"), LineHeight = 20 });
        var list = details?.ToList();
        if (list is { Count: > 0 })
        {
            var box = new ListBox
            {
                ItemsSource = list.Take(500).ToList(),
                MaxHeight = 220,
                Margin = new Thickness(0, 12, 0, 0),
                Background = ThemeManager.Brush("Surface2Bg"),
                BorderThickness = new Thickness(0),
                Foreground = ThemeManager.Brush("TextSecondary"),
                FontSize = 12,
                Padding = new Thickness(6),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Disabled);
            body.Children.Add(box);
            if (list.Count > 500)
                body.Children.Add(new TextBlock { Text = $"… ve {list.Count - 500} öğe daha", Foreground = ThemeManager.Brush("TextMuted"), Margin = new Thickness(0, 6, 0, 0) });
        }
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        bool? result = null;
        if (cancelText != null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("SecondaryButton") };
            cancel.Click += (_, _) => { result = false; win.Close(); };
            buttons.Children.Add(cancel);
        }
        var ok = new Button
        {
            Content = okText, IsDefault = true, MinWidth = 96,
            Style = (Style)Application.Current.FindResource(kind == Kind.Danger ? "DangerButton" : "PrimaryButton"),
        };
        ok.Click += (_, _) => { result = true; win.Close(); };
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        win.Content = root;
        win.ShowDialog();
        return cancelText == null ? true : result;
    }
}
