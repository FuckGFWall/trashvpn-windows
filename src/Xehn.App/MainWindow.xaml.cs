using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Xehn.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public MainWindow()
    {
        InitializeComponent();
        model = new MainViewModel(); DataContext = model;
        timer.Tick += async (_, _) => await model.RefreshAsync();
        Loaded += async (_, _) =>
        {
            await model.RefreshAsync(); timer.Start();
            model.InitializePreparation();
            if (model.AutoConnect && !model.IsConnected) await model.ToggleAsync();
        };
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        Closing += OnClosing;
        Closed += (_, _) => { timer.Stop(); SystemEvents.UserPreferenceChanged -= SystemThemeChanged; model.Dispose(); };
    }
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.InvokeAsync(() => { if (model.Theme == "auto") App.ApplyTheme("auto"); });
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (HomePage == null) return;
        var page = (string)((RadioButton)sender).Tag;
        PageHeader.Visibility = page == "Home" ? Visibility.Collapsed : Visibility.Visible;
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "Settings" => "设置", "About" => "关于", "Logs" => "日志", _ => "首页" };
    }
    private async void ToggleConnection(object sender, RoutedEventArgs e) => await model.ToggleAsync();
    private async void CheckGeo(object sender, RoutedEventArgs e) => await model.GeoAsync(false);
    private async void RepairGeo(object sender, RoutedEventArgs e) => await model.GeoAsync(true);
    private void OpenLink(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo((string)((Button)sender).Tag) { UseShellExecute = true }); }
        catch (Exception ex) { model.Notice = ex.Message; }
    }
    private void OpenNotices(object sender, RoutedEventArgs e)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { model.Notice = ex.Message; }
    }
    private void CopyLogs(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(model.Logs.Length > 0 ? model.Logs : "暂无运行日志"); }
        catch (Exception ex) { model.Notice = ex.Message; }
    }
    private bool closingPreparation;
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (model.IsPreparing && !model.ConnectionRequested)
        {
            e.Cancel = true;
            if (closingPreparation) return;
            closingPreparation = true;
            await model.StopPreparationAsync();
            closingPreparation = false; Close(); return;
        }
        // Do not kill an in-flight engine or leave a system proxy pointing at a dead core.
        if (model.IsBusy || model.IsConnected)
        {
            e.Cancel = true;
            model.Notice = model.IsBusy ? "请先取消当前操作，等待清理完成后再关闭。" : "请先点击电源断开连接，再关闭客户端。";
        }
    }
}
