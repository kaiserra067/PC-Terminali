using System.Windows;
using System.Windows.Controls;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public partial class HistoryView : UserControl, IPage
{
    public HistoryView()
    {
        InitializeComponent();
        ActivityLog.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Load(); });
    }

    public void OnShown() => Load();

    private void Load()
    {
        var items = ActivityLog.ReadAll();
        List.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TotalText.Text = Fmt.Size(items.Sum(i => i.Bytes));
        CountText.Text = Fmt.Count(items.Count);
        LastText.Text = items.Count > 0 ? Fmt.Ago(items[0].Time) : "—";
    }

    private void Backups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.RegistryBackupDir);
        PathUtil.OpenInExplorer(AppPaths.RegistryBackupDir);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("Geçmiş temizlensin mi?", "İşlem geçmişi listesi silinecek. Kayıt defteri yedekleri korunur.", "Temizle")) return;
        ActivityLog.Clear();
        Load();
    }
}
