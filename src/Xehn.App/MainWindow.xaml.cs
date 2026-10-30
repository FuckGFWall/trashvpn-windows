using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace Xehn.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TrayService? tray;
    private bool allowClose, exitInProgress, loaded, closed;
    public MainWindow()
    {
        InitializeComponent();
        model = new MainViewModel(); DataContext = model;
        timer.Tick += async (_, _) => await model.RefreshAsync();
        Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            try { tray = new TrayService(model, ShowMainWindow, RequestExitAsync); }
            catch (Exception ex) { model.Notice = "托盘初始化失败，关闭窗口将退出：" + ex.Message; }
            await model.InitializeResourcesAsync();
            if (closed || exitInProgress) return;
            await model.RefreshAsync(); timer.Start();
            if (closed || exitInProgress) { timer.Stop(); return; }
            model.InitializePreparation();
            if (((App)Application.Current).StartupNotice.Length > 0) model.Notice = ((App)Application.Current).StartupNotice;
            if ((((App)Application.Current).RestartConnection ?? model.AutoConnect) && !model.IsConnected) await model.ToggleAsync();
        };
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; timer.Stop(); tray?.Dispose(); SystemEvents.UserPreferenceChanged -= SystemThemeChanged; model.Dispose(); };
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
    private async void ResourceAction(object sender, RoutedEventArgs e) => await model.RunResourceActionAsync((string)((Button)sender).Tag);
    private void NavigateProject(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { model.Notice = ex.Message; }
        e.Handled = true;
    }
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
    public void ShowMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    public static bool ShouldHideOnClose(bool closeToTray, bool trayAvailable, bool explicitExit) => closeToTray && trayAvailable && !explicitExit;
    public async Task RequestExitAsync()
    {
        if (exitInProgress) return;
        exitInProgress = true;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!await model.ShutdownAsync()) { ShowMainWindow(); return; }
            allowClose = true;
            Close(); Application.Current.Shutdown();
        }
        finally { exitInProgress = false; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose || !loaded) return;
        // Shutdown after an elevation handoff must not be converted into Hide.
        if (((App)Application.Current).IsRestarting) return;
        e.Cancel = true;
        if (ShouldHideOnClose(model.CloseToTray, tray != null, exitInProgress)) { Hide(); return; }
        await RequestExitAsync();
    }
}
