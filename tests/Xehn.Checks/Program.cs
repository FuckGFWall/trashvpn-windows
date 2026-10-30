using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xehn.App;

internal static class Program
{
    private static int count;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); count++;
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; Application.Current?.Shutdown(1); }
    }
    private static void Run(string[] args)
    {
        Check(ClientSettings.Speed(null) == "" && ClientSettings.Speed(0) == "", "no traffic stays blank");
        Check(ClientSettings.Speed(1048576) == "1.00 MB/s", "traffic units");
        var settings = new ClientSettings();
        Check(settings.Validate() == "", "valid defaults");
        settings.ApiPort = settings.MixedPort;
        Check(settings.Validate().Length > 0, "reject port collision");
        settings.ApiPort = 9090; settings.Mtu = 500;
        Check(settings.Validate().Length > 0, "reject invalid MTU");
        settings.Mtu = 0; settings.Proto = "wwarp"; settings.IpFamily = "v6";
        Check(settings.Validate().Length > 0, "nested protocol needs IPv4");
        settings.Proto = "awg"; settings.IpFamily = "v4"; settings.ProxyMode = "bypass";
        var json = JsonSerializer.Serialize(settings, ClientSettings.JsonOptions);
        Check(json.Contains("\"proxy_mode\": \"bypass\"") && json.Contains("\"api_port\""), "Go settings wire format");
        Check(!json.Contains("control_code"), "no remote-control identity");
        var outDir = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/previews");
        Directory.CreateDirectory(outDir);
        var app = new App { LaunchWindowOnStartup = false }; app.InitializeComponent();
        using (var logo = Application.GetResourceStream(new Uri("pack://application:,,,/Xehn.App;component/Assets/logo.ico")).Stream)
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(logo)).Equals("2c8a810abb2d6d13224c690e99111ac3296aec39514f6180729675768a21aac7", StringComparison.OrdinalIgnoreCase), "embedded ICO exactly matches upstream download");
        WorkflowChecks.Run(Check, outDir);
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        void Layout() { app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); root.Measure(new Size(1120, 780)); root.Arrange(new Rect(0, 0, 1120, 780)); root.UpdateLayout(); }
        void Snapshot(string name)
        {
            Layout();
            var bitmap = new RenderTargetBitmap(1120, 780, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(outDir, name + ".png")); png.Save(stream);
        }
        Layout();
        var vm = (MainViewModel)window.DataContext;
        Check(vm.Upload == "" && vm.Download == "", "initial UI has no fake traffic");
        Check(((FrameworkElement)window.FindName("PageHeader")).Visibility == Visibility.Collapsed, "home header removed");
        Check(window.FindName("PageCaption") == null && !Find<TextBlock>(root).Any(t => t.Text == "WARP, SIMPLIFIED."), "decorative subtitles removed");
        Check(((RadioButton)window.FindName("HomeNav")).ActualHeight >= 56, "larger sidebar selection target");
        Check(Find<ProgressBar>((FrameworkElement)window.FindName("HomePage")).Count() == 2, "home shows registration and scan progress bars");
        Check(ReferenceEquals(window.Icon, Branding.Image) && ReferenceEquals(((Image)window.FindName("BrandIcon")).Source, Branding.Image), "window and sidebar render original upstream icon");
        Check(((FrameworkElement)window.FindName("ModeSelectors")).GetBindingExpression(UIElement.IsEnabledProperty)?.ParentBinding.Path.Path == "CanChangeModes", "mode selector binding permits connected hot switching");
        Snapshot("home-light");
        foreach (var page in new[] { "Settings", "About", "Logs", "Home" })
        {
            var nav = Find<RadioButton>(root).Single(b => (string?)b.Tag == page);
            nav.IsChecked = true; Layout();
            Check(((FrameworkElement)window.FindName(page + "Page")).Visibility == Visibility.Visible, page + " navigation");
            Snapshot(page.ToLowerInvariant() + "-light");
            if (page == "Settings")
            {
                var panel = (FrameworkElement)window.FindName("SettingsPage");
                Check(((CheckBox)window.FindName("CloseToTraySetting")).IsChecked == true && window.FindName("AutoStartSetting") is CheckBox, "settings expose default-on tray closing and autostart");
                Check(Find<ComboBox>(panel).All(c => c.ActualHeight == 32 && c.ActualWidth == 160), "compact dropdown dimensions");
                Check(Find<TextBox>(panel).All(t => t.ActualHeight == 32), "compact input height");
                App.ApplyTheme("dark"); Snapshot("settings-dark");
                App.ApplyTheme("light"); Layout();
            }
            if (page == "About")
            {
                var about = (Grid)window.FindName("AboutPage");
                Check(about.RowDefinitions.Count == 2 && about.ColumnDefinitions.Count == 2 && about.Children.OfType<Border>().Count() == 4, "about page is a four-card 2x2 layout");
                Check(((TextBlock)window.FindName("ProductVersion")).Text == "1.0.0", "about page shows app version 1.0.0");
                Check(window.FindName("CoreDownloadBar") is ProgressBar && window.FindName("GeoDownloadBar") is ProgressBar && window.FindName("CoreResourceStatus") is TextBlock && window.FindName("GeoResourceStatus") is TextBlock, "each resource card has its own download bar and replacement-status text");
                var project = Find<TextBlock>((Border)window.FindName("ProductCard")).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
                Check(project.NavigateUri.AbsoluteUri == "https://github.com/FuckGFWall/trashvpn-windows" && !new System.Windows.Documents.TextRange(project.ContentStart, project.ContentEnd).Text.Contains("https://"), "project hyperlink hides raw URL and targets this repository");
                App.ApplyTheme("dark"); Snapshot("about-dark"); App.ApplyTheme("light"); Layout();
            }
        }
        App.ApplyTheme("dark"); Snapshot("home-dark");
        App.ApplyTheme("light");
        Check(((Button)window.FindName("PowerButton")).Content == null, "power button contains no connection text");
        using (var tray = new TrayService(vm, () => { }, () => Task.CompletedTask, visible: false))
        {
            var menu = tray.ContextMenu;
            void MenuSnapshot(string name)
            {
                menu.ApplyTemplate(); menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(menu); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(outDir, name + ".png")); png.Save(stream);
            }
            MenuSnapshot("tray-menu-light");
            var surface = (Border)menu.Template.FindName("TrayMenuSurface", menu);
            Check(surface.CornerRadius.TopLeft == 12 && ReferenceEquals(menu.Background, app.FindResource("PageBrush")), "tray menu uses rounded themed surface instead of legacy white strip");
            Check(menu.Items.Cast<MenuItem>().All(i => Find<Image>(i).Count() == 0 && Find<Grid>(i).Count() == 0), "tray items have no reserved icon / white rectangle column");
            App.ApplyTheme("dark"); MenuSnapshot("tray-menu-dark");
            Check(ReferenceEquals(menu.Background, app.FindResource("PageBrush")), "tray menu follows dark theme resource changes");
            App.ApplyTheme("light");
        }
        using (var previewModel = new MainViewModel(new EngineService(Path.Combine(outDir, "resource-preview", Guid.NewGuid().ToString("N"))), resourceService: new PreviewResources()))
        {
            previewModel.InitializeResourcesAsync().GetAwaiter().GetResult();
            window.DataContext = previewModel;
            Find<RadioButton>(root).Single(b => (string?)b.Tag == "About").IsChecked = true;
            var coreDownload = previewModel.RunResourceActionAsync("resources-download-core");
            Layout(); Snapshot("about-core-download-light");
            Check(((ProgressBar)window.FindName("CoreDownloadBar")).Visibility == Visibility.Visible && ((ProgressBar)window.FindName("CoreDownloadBar")).Value == 50, "core card renders live byte progress");
            PreviewResources.Complete(previewModel, coreDownload, Layout);
            var geoDownload = previewModel.RunResourceActionAsync("resources-download-geo");
            Layout(); Snapshot("about-geo-download-light");
            Check(((ProgressBar)window.FindName("GeoDownloadBar")).Visibility == Visibility.Visible && ((TextBlock)window.FindName("CoreResourceStatus")).Text.Contains("成功"), "GeoFiles card renders progress while installed core status stays visible");
            PreviewResources.Complete(previewModel, geoDownload, Layout);
            Snapshot("about-resources-updated");
            window.DataContext = vm;
        }
        vm.Dispose(); window.Close(); app.Shutdown();
        Console.WriteLine($"{count} checks passed; UI previews: {outDir}");
    }

    private sealed class PreviewResources : IResourceService
    {
        private static PreviewResources? active;
        private TaskCompletionSource<ResourceInfo>? pending;
        private ResourceInfo current = new("1.19.33", false, "全部协议检查通过", "2026-10-20", true, false);
        private string command = "";
        public PreviewResources() => active = this;
        public Task<ResourceInfo> RunAsync(string command, Action<string> log, Action<ResourceProgress>? progress = null)
        {
            this.command = command;
            if (command.Contains("download", StringComparison.Ordinal))
            {
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                progress?.Invoke(new(command.EndsWith("core") ? "core" : "geo", "download", command.EndsWith("core") ? "mihomo.zip" : "geosite.dat", 1048576, 2097152));
                return pending.Task;
            }
            if (command == "resources-apply") { current = current with { GeoPending = false, GeoNeedsDownload = false, GeoUpdated = "2026-10-30" }; progress?.Invoke(new("geo", "installed", "", 1, 1)); }
            return Task.FromResult(current);
        }
        public static void Complete(MainViewModel model, Task task, Action layout)
        {
            var self = active!;
            if (self.command.Contains("geo")) self.current = self.current with { GeoPending = true };
            self.pending!.SetResult(self.current);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Resource preview did not settle"); layout(); Thread.Sleep(1); }
            task.GetAwaiter().GetResult();
        }
        public Task CancelAsync() { pending?.TrySetCanceled(); return Task.CompletedTask; }
        public void Dispose() { }
    }
}
