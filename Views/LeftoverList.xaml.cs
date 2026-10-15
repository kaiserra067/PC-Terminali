using System.Collections;
using System.Windows;
using System.Windows.Controls;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

/// <summary>Kalıntı listesi (Kalıntılar sayfası ve kaldırma sonrası pencerede ortak).</summary>
public partial class LeftoverList : UserControl
{
    public event Action? SelectionChanged;

    public LeftoverList()
    {
        InitializeComponent();
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(() => List.Items.Refresh());
    }

    public IEnumerable? ItemsSource
    {
        get => List.ItemsSource;
        set => List.ItemsSource = value;
    }

    private void Check_Click(object sender, RoutedEventArgs e) => SelectionChanged?.Invoke();

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LeftoverItem item }) return;
        if (item.IsRegistry)
        {
            try { Clipboard.SetText(item.Path); } catch { }
            Dialogs.Info("Kayıt defteri anahtarı", "Anahtar yolu panoya kopyalandı. Kayıt Defteri Düzenleyicisi'nde (regedit) bu yolu adres çubuğuna yapıştırabilirsiniz.", new[] { item.Path });
        }
        else PathUtil.OpenInExplorer(item.Path);
    }
}
