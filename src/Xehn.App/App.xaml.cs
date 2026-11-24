using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Xehn.App;
public partial class App : Application
{
    public static event Action? ThemeChanged;
    public static bool IsDarkTheme { get; private set; }
    private Mutex? instance;
    public bool LaunchWindowOnStartup { get; set; } = true;
    public bool RestartConnection { get; private set; }
    public bool IsRestarting { get; set; }
    public string StartupNotice { get; private set; } = "";
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!LaunchWindowOnStartup) return;
        if (e.Args.Length > 0)
        {
            if (!await DesktopIntegration.WaitForRestartAsync(e.Args)) { Shutdown(); return; }
            RestartConnection = e.Args[3] == "1";
        }
        instance = new Mutex(true, @"Local\Xehn.Fluent.Desktop", out var first);
        if (!first) { Shutdown(); return; }
        var desktop = new DesktopIntegration();
        var portable = await PortableStartup.CheckAsync(AppContext.BaseDirectory, desktop);
        if (portable.Stop)
        {
            if (portable.Error is { } error)
            {
                MessageBox.Show(Localization.Text(error), "TrashVPN", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        try { AppPaths.PrepareInstalledData(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Localization.Text("安装数据初始化失败：" + ex.Message), "TrashVPN", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        if (e.Args.Length == 0)
        {
            try
            {
                var settings = ClientSettings.Load(AppPaths.DataHome);
                if (DesktopIntegration.NeedsStartupElevation(settings.CaptureMode, desktop.IsElevated))
                {
                    if (await desktop.RestartElevatedAsync(false, () => Task.FromResult(true))) return;
                    StartupNotice = "已取消管理员启动；TUN 连接仍需管理员授权。";
                }
            }
            catch (Exception ex) { StartupNotice = "管理员启动未完成：" + ex.Message; }
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
    public static void ApplyTheme(string value)
    {
        // Let WPF update its own Fluent / native-frame theme state as well as
        // the resource brushes; swapping dictionaries alone leaves two theme
        // owners that can overwrite each other on a later dispatcher pass.
        Current.ThemeMode = value switch { "dark" => ThemeMode.Dark, "auto" => ThemeMode.System, _ => ThemeMode.Light };
        var dark = value == "dark" || value == "auto" && (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
        IsDarkTheme = dark;
        string[] keys = ["PageBrush", "CardBrush", "RailBrush", "LineBrush", "InkBrush", "MutedBrush", "TintBrush", "PowerIdleBrush"];
        string[] colors = dark ? ["#20211F", "#292B28", "#1B1D1B", "#3C3E39", "#F3F3EF", "#ADB0A7", "#432D20", "#62665F"] : ["#F7F7F5", "#FFFFFF", "#F0F0ED", "#E8E7E3", "#242622", "#777A73", "#FFF0E5", "#858A83"];
        for (var i = 0; i < keys.Length; i++) Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        string[] shellKeys = ["NavigationPaneBrush", "NavigationSelectedBrush", "NavigationHoverBrush", "NavigationPressedBrush", "ShellStrokeBrush", "AboutSurfaceBrush", "AboutFooterBrush"];
        string[] shellColors = dark ? ["#202020", "#10FFFFFF", "#0BFFFFFF", "#15FFFFFF", "#20FFFFFF", "#2B2B2B", "#10000000"] : ["#F3F3F3", "#0C000000", "#08000000", "#10000000", "#12000000", "#FFFFFF", "#03000000"];
        for (var i = 0; i < shellKeys.Length; i++) Current.Resources[shellKeys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(shellColors[i]));
        ThemeChanged?.Invoke();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
