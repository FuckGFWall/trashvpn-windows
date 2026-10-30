using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Navigation;
using System.Windows.Media;
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
        SourceInitialized += (_, _) => { WindowPresentation.FitToWorkArea(this, center: true); UpdateTitleBar(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!closed) WindowPresentation.FitToWorkArea(this, center: false); }));
        App.ThemeChanged += UpdateTitleBar;
        timer.Tick += async (_, _) => await model.RefreshAsync();
        Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            WindowPresentation.FitToWorkArea(this, center: false); UpdateTitleBar();
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
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; timer.Stop(); tray?.Dispose(); App.ThemeChanged -= UpdateTitleBar; SystemEvents.UserPreferenceChanged -= SystemThemeChanged; SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged; model.Dispose(); };
    }
    private void UpdateTitleBar() => WindowPresentation.ApplyTitleBar(this, App.IsDarkTheme,
        ((SolidColorBrush)FindResource("PageBrush")).Color, ((SolidColorBrush)FindResource("InkBrush")).Color);
    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => { if (!closed) WindowPresentation.FitToWorkArea(this, center: false); });
    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.InvokeAsync(() => { if (closed) return; if (model.Theme == "auto") App.ApplyTheme("auto"); WindowPresentation.FitToWorkArea(this, center: false); });
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (HomePage == null) return;
        var page = (string)((RadioButton)sender).Tag;
        PageScroller.Visibility = page is "Settings" or "About" ? Visibility.Visible : Visibility.Collapsed;
        PageHeader.Visibility = page == "Home" ? Visibility.Collapsed : Visibility.Visible;
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        HomeViewport.Visibility = HomePage.Visibility;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "Settings" => "设置", "About" => "关于", "Logs" => "日志", _ => "首页" };
        // Navigation is a fresh visit, not a continuation of another page's
        // offset. Reset again after layout in case its extent just changed.
        PageScroller.ScrollToTop();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!closed && PageTitle.Text == (page switch { "Settings" => "设置", "About" => "关于", "Logs" => "日志", _ => "首页" }))
            {
                PageScroller.ScrollToTop(); LogScroller.ScrollToTop(); LogText.ScrollToHome();
            }
        }));
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
        timer.Stop();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!await model.ShutdownAsync()) { ShowMainWindow(); return; }
            allowClose = true;
            Close(); Application.Current.Shutdown();
        }
        finally { exitInProgress = false; if (!closed && loaded) timer.Start(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose || !loaded && new System.Windows.Interop.WindowInteropHelper(this).Handle == 0) return;
        // Shutdown after an elevation handoff must not be converted into Hide.
        if (((App)Application.Current).IsRestarting) return;
        e.Cancel = true;
        if (ShouldHideOnClose(model.CloseToTray, tray != null, exitInProgress)) { Hide(); return; }
        await RequestExitAsync();
    }
}
