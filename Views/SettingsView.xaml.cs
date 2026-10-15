using System.Windows;
using System.Windows.Controls;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public partial class SettingsView : UserControl, IPage
{
    private bool _init;

    public SettingsView()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        (s.Theme switch { ThemeChoice.Light => ThemeLight, ThemeChoice.Dark => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        (s.UseRecycleBin ? ModeRecycle : ModePermanent).IsChecked = true;
        foreach (ComboBoxItem it in AgeCombo.Items) if (it.Tag as string == s.TempMinAgeDays.ToString()) it.IsSelected = true;
        if (AgeCombo.SelectedIndex < 0) AgeCombo.SelectedIndex = 1;
        var ver = typeof(SettingsView).Assembly.GetName().Version;
        AboutText.Text = $"PC Terminali {ver?.ToString(3)} · .NET {Environment.Version} · {(Admin.IsElevated ? "Yönetici modunda çalışıyor" : "Normal modda çalışıyor")}\n" +
                         $"Ayarlar, işlem geçmişi ve kayıt defteri yedekleri: {AppPaths.DataDir}";
        RefreshExclusions();
        _init = true;
    }

    public void OnShown() { }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        var choice = ThemeLight.IsChecked == true ? ThemeChoice.Light : ThemeDark.IsChecked == true ? ThemeChoice.Dark : ThemeChoice.System;
        AppSettings.Current.Theme = choice;
        AppSettings.Current.Save();
        ThemeManager.Apply(choice);
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        AppSettings.Current.UseRecycleBin = ModeRecycle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void Age_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init || AgeCombo.SelectedItem is not ComboBoxItem { Tag: string t }) return;
        AppSettings.Current.TempMinAgeDays = int.Parse(t);
        AppSettings.Current.Save();
        AppState.I.LastJunk = null;
    }

    private void RefreshExclusions()
    {
        ExclusionList.ItemsSource = AppSettings.Current.Exclusions.ToList();
        NoExclusions.Visibility = AppSettings.Current.Exclusions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Hiçbir zaman dokunulmayacak klasörü seçin" };
        if (dlg.ShowDialog() != true) return;
        if (!AppSettings.Current.Exclusions.Contains(dlg.FolderName, PathUtil.Cmp)) AppSettings.Current.Exclusions.Add(dlg.FolderName);
        AppSettings.Current.Save();
        RefreshExclusions();
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string p) return;
        AppSettings.Current.Exclusions.RemoveAll(x => PathUtil.Cmp.Equals(x, p));
        AppSettings.Current.Save();
        RefreshExclusions();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        PathUtil.OpenInExplorer(AppPaths.DataDir);
    }
}
