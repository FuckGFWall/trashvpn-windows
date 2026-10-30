using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Xehn.App;
public partial class App : Application
{
    private Mutex? instance;
    public bool LaunchWindowOnStartup { get; set; } = true;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!LaunchWindowOnStartup) return;
        instance = new Mutex(true, @"Local\Xehn.Fluent.Desktop", out var first);
        if (!first) { Shutdown(); return; }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
    public static void ApplyTheme(string value)
    {
        var theme = value switch { "dark" => "Fluent.Dark.xaml", "auto" => "Fluent.xaml", _ => "Fluent.Light.xaml" };
        Current.Resources.MergedDictionaries.Clear();
        Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/" + theme) });
        var dark = value == "dark" || value == "auto" && (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
        string[] keys = ["PageBrush", "CardBrush", "RailBrush", "LineBrush", "InkBrush", "MutedBrush", "TintBrush"];
        string[] colors = dark ? ["#20211F", "#292B28", "#1B1D1B", "#3C3E39", "#F3F3EF", "#ADB0A7", "#432D20"] : ["#F7F7F5", "#FFFFFF", "#F0F0ED", "#E8E7E3", "#242622", "#777A73", "#FFF0E5"];
        for (var i = 0; i < keys.Length; i++) Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
