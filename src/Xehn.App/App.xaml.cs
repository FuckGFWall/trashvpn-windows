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
    public bool? RestartConnection { get; private set; }
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
        if (e.Args.Length == 0)
        {
            try
            {
                var settings = ClientSettings.Load(Path.Combine(AppContext.BaseDirectory, "data"));
                var desktop = new DesktopIntegration();
                if (DesktopIntegration.NeedsStartupElevation(settings.CaptureMode, desktop.IsElevated))
                {
                    if (await desktop.RestartElevatedAsync(settings.AutoConnect, () => Task.FromResult(true))) return;
                    StartupNotice = "已取消管理员启动；TUN 连接仍需管理员授权。";
                    RestartConnection = false; // do not prompt a second time via AutoConnect
                }
            }
            catch (Exception ex) { StartupNotice = "管理员启动未完成：" + ex.Message; RestartConnection = false; }
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
        string[] keys = ["PageBrush", "CardBrush", "RailBrush", "LineBrush", "InkBrush", "MutedBrush", "TintBrush"];
        string[] colors = dark ? ["#20211F", "#292B28", "#1B1D1B", "#3C3E39", "#F3F3EF", "#ADB0A7", "#432D20"] : ["#F7F7F5", "#FFFFFF", "#F0F0ED", "#E8E7E3", "#242622", "#777A73", "#FFF0E5"];
        for (var i = 0; i < keys.Length; i++) Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        ThemeChanged?.Invoke();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
