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
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        using (var logo = Application.GetResourceStream(new Uri("pack://application:,,,/Xehn.App;component/Assets/logo.ico")).Stream)
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(logo)).Equals("2c8a810abb2d6d13224c690e99111ac3296aec39514f6180729675768a21aac7", StringComparison.OrdinalIgnoreCase), "embedded ICO exactly matches upstream download");
        WorkflowChecks.Run(Check, outDir);
        DashboardChecks.Run(Check);
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        double viewWidth = 980, viewHeight = 720;
        void Layout() { app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); root.Measure(new Size(viewWidth, viewHeight)); root.Arrange(new Rect(0, 0, viewWidth, viewHeight)); root.UpdateLayout(); }
        void Snapshot(string name)
        {
            // This visual tree is deliberately not a shown client window, so
            // explicitly render the controllable clock's current chart frame.
            foreach (var chart in Find<TrafficChart>(root)) chart.InvalidateVisual();
            Layout();
            var bitmap = new RenderTargetBitmap((int)viewWidth, (int)viewHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(outDir, name + ".png")); png.Save(stream);
        }
        Layout();
        var vm = (MainViewModel)window.DataContext;
        Check(vm.Upload == "" && vm.Download == "", "initial UI has no fake traffic");
        Check(((FrameworkElement)window.FindName("PageHeader")).Visibility == Visibility.Collapsed, "home header removed");
        Check(window.FindName("PageCaption") == null && !Find<TextBlock>(root).Any(t => t.Text == "WARP, SIMPLIFIED."), "decorative subtitles removed");
        Check(window.Width == 980 && window.Height == 760 && ((Grid)root).ColumnDefinitions[0].ActualWidth == 156, "smaller window and narrower sidebar");
        Check(((TextBlock)window.FindName("PageTitle")).FontSize == 30, "Settings / About / Logs page heading keeps its original font size");
        Check(((RadioButton)window.FindName("HomeNav")).ActualHeight == 44 && ((RadioButton)window.FindName("HomeNav")).ActualWidth is >= 130 and <= 132, "compact sidebar selection background");
        Check(Find<TextBlock>((RadioButton)window.FindName("HomeNav")).Select(t => t.FontSize).SequenceEqual(new[] { 20d, 16d }), "sidebar navigation icon and label fonts are smaller");
        Check(Find<ProgressBar>((FrameworkElement)window.FindName("HomePage")).Count() == 2, "home shows registration and scan progress bars");
        Check(ReferenceEquals(window.Icon, Branding.Image) && ReferenceEquals(((Image)window.FindName("BrandIcon")).Source, Branding.Image), "window and sidebar render original upstream icon");
        Check(((FrameworkElement)window.FindName("ModeSelectors")).GetBindingExpression(UIElement.IsEnabledProperty)?.ParentBinding.Path.Path == "CanChangeModes", "mode selector binding permits connected hot switching");
        var home = (FrameworkElement)window.FindName("HomePage"); var viewport = (FrameworkElement)window.FindName("PageContent");
        bool HasScrollAncestor(DependencyObject node) { for (var p = VisualTreeHelper.GetParent(node); p != null; p = VisualTreeHelper.GetParent(p)) if (p is ScrollViewer) return true; return false; }
        Check(!HasScrollAncestor(home) && ((ScrollViewer)window.FindName("PageScroller")).Visibility == Visibility.Collapsed, "home is outside the scrolling region, not just hiding its scrollbar");
        Check(home.DesiredSize.Height <= viewport.ActualHeight, "all home cards fit the compact default window without clipping");
        var prep = (FrameworkElement)window.FindName("PreparationPanel"); var modes = (FrameworkElement)window.FindName("ModeSelectors");
        Check(modes.TranslatePoint(new Point(), home).Y - prep.TranslatePoint(new Point(0, prep.ActualHeight), home).Y >= 40, "mode sliders leave more space below the preparation progress area");
        var power = (Button)window.FindName("PowerButton");
        Check(power.ActualWidth == 116 && ((Viewbox)power.Template.FindName("PowerGlyph", power)).Width == 54, "smaller power button preserves the original white power glyph size");
        Snapshot("home-light");
        viewWidth = 880; viewHeight = 680; vm.Notice = string.Concat(Enumerable.Repeat("较长的连接提示，完整内容保留在悬浮提示中。", 8)); Layout();
        Check(home.DesiredSize.Height <= viewport.ActualHeight && ((FrameworkElement)window.FindName("TrafficCard")).TranslatePoint(new Point(0, ((FrameworkElement)window.FindName("TrafficCard")).ActualHeight), viewport).Y <= viewport.ActualHeight, "minimum window with a long notice still fits every home card");
        Snapshot("home-minimum"); viewWidth = 980; viewHeight = 720; vm.Notice = ""; Layout();
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
                Check(Find<ComboBox>(panel).All(c => c.ActualHeight == 28 && c.ActualWidth == 148), "smaller dropdown dimensions");
                Check(Find<TextBox>(panel).All(t => t.ActualHeight == 28 && t.ActualWidth == 148), "all inputs including custom DNS share compact dimensions");
                Check(Find<ComboBox>(panel).All(c => c.FontSize == 13) && Find<TextBox>(panel).All(t => t.FontSize == 13) && Find<CheckBox>(panel).All(c => c.FontSize == 13) && Find<TextBlock>(panel).Single(t => t.Text == "自定义 DNS").FontSize == 14, "settings text sizes stay unchanged while control boxes shrink");
                App.ApplyTheme("dark"); Snapshot("settings-dark");
                App.ApplyTheme("light"); Layout();
            }
            if (page == "About")
            {
                var about = (Grid)window.FindName("AboutPage");
                Check(about.RowDefinitions.Count == 2 && about.ColumnDefinitions.Count == 2 && about.Children.OfType<Border>().Count() == 4, "about page is a four-card 2x2 layout");
                Check(((TextBlock)window.FindName("ProductVersion")).Text == "1.0.0", "about page shows app version 1.0.0");
                Check(((TextBlock)window.FindName("ProductVersion")).FontSize == 18 && Find<TextBlock>((Border)window.FindName("ProductCard")).Single(t => t.Text == "XEHN").FontSize == 28, "About page retains its original text sizes");
                Check(window.FindName("CoreDownloadBar") is ProgressBar && window.FindName("GeoDownloadBar") is ProgressBar && window.FindName("CoreResourceStatus") is TextBlock && window.FindName("GeoResourceStatus") is TextBlock, "each resource card has its own download bar and replacement-status text");
                var project = Find<TextBlock>((Border)window.FindName("ProductCard")).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
                Check(project.NavigateUri.AbsoluteUri == "https://github.com/FuckGFWall/trashvpn-windows" && !new System.Windows.Documents.TextRange(project.ContentStart, project.ContentEnd).Text.Contains("https://"), "project hyperlink hides raw URL and targets this repository");
                App.ApplyTheme("dark"); Snapshot("about-dark"); App.ApplyTheme("light"); Layout();
            }
            if (page == "Logs") Check(Find<TextBox>((FrameworkElement)window.FindName("LogsPage")).Single().FontSize == 12 && Find<TextBlock>((FrameworkElement)window.FindName("LogsPage")).Single(t => t.Text == "运行日志").FontSize == 16, "Logs page retains its original text sizes");
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
            Check(menu.ActualWidth >= 130 && menu.ActualWidth < 160 && menu.FontSize == 12, "tray menu is narrower with smaller text and no excessive right whitespace");
            Check(menu.Items.Cast<MenuItem>().All(i => Find<Image>(i).Count() == 0 && Find<Grid>(i).Count() == 0), "tray items have no reserved icon / white rectangle column");
            App.ApplyTheme("dark"); MenuSnapshot("tray-menu-dark");
            Check(ReferenceEquals(menu.Background, app.FindResource("PageBrush")), "tray menu follows dark theme resource changes");
            App.ApplyTheme("light");
        }
        using (var preview = new DashboardPreviewEngine(Path.Combine(outDir, "dashboard-preview", Guid.NewGuid().ToString("N"))))
        {
            var time = new ManualTimeProvider();
            using var model = new MainViewModel(preview, timeProvider: time);
            window.DataContext = model;
            for (var i = 0; i < 32; i++) { model.UploadSeries.Add(i % 7 == 0 ? 0 : 3000 + 9000 * (1 + Math.Sin(i * .55))); model.DownloadSeries.Add(i % 8 == 0 ? 0 : 10000 + 45000 * (1 + Math.Cos(i * .45))); time.Advance(2); }
            model.InitializePreparation();
            preview.Emit("@@XEHN {\"type\":\"progress\",\"phase\":\"scan\",\"text\":\"验证隧道 · 完成 4/14 · 可用 2/2 · 跳过 10\\n正在准备并复用已有账号与优选结果\",\"done\":4,\"total\":14}");
            var connect = model.ToggleAsync(); Layout();
            Snapshot("home-connecting");
            var glyph = (Viewbox)power.Template.FindName("PowerGlyph", power); var ring = (ProgressRing)power.Template.FindName("PowerProgressRing", power);
            Check(glyph.Visibility == Visibility.Collapsed && ring.IsActive && ((ProgressRing)window.FindName("StatusProgressRing")).IsActive, "connection replaces power glyph with a ring and shows a small sidebar ring");
            preview.Complete(); DashboardPreviewEngine.Wait(() => preview.Command == "desktop-connect", Layout);
            preview.Connected = true; preview.Emit("@@XEHN {\"type\":\"connection\",\"state\":\"provisional\"}"); Layout();
            Check(((TextBlock)window.FindName("ConnectionHeading")).Text == "已连接" && ring.IsActive, "actual home heading hides verification wording without stopping real verification");
            Snapshot("home-verifying");
            preview.Emit("@@XEHN {\"type\":\"connection\",\"state\":\"verified\"}"); preview.Complete(); DashboardPreviewEngine.Wait(() => connect.IsCompleted, Layout); connect.GetAwaiter().GetResult(); Layout();
            Check(!ring.IsActive && glyph.Visibility == Visibility.Visible, "verified connection restores the unchanged white power glyph");
            preview.Connected = false; time.Advance(2); model.RefreshAsync().GetAwaiter().GetResult(); Snapshot("home-traffic-retained");
            time.Advance(12); Snapshot("home-traffic-aged");
            window.DataContext = vm;
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

    private sealed class DashboardPreviewEngine(string home) : IEngineService
    {
        public string Home => home;
        public bool Connected;
        public string Command = "";
        private TaskCompletionSource? active;
        private Action<string>? log;
        public Task RunAsync(string command, Action<string> log, string? settingsJson = null, bool prepared = false) { Command = command; this.log = log; active = new(TaskCreationOptions.RunContinuationsAsynchronously); return active.Task; }
        public void Emit(string line) => log?.Invoke(line);
        public void Complete() => active!.TrySetResult();
        public Task CancelAsync() { active?.TrySetCanceled(); return Task.CompletedTask; }
        public Task<Session> ReadSessionAsync() => Task.FromResult(new Session(Connected));
        public Task<(double Up, double Down)> TrafficAsync(int port) => Task.FromResult((0d, 0d));
        public void Dispose() { }
        public static void Wait(Func<bool> ready, Action layout) { var deadline = DateTime.UtcNow.AddSeconds(5); while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Dashboard fixture did not settle"); layout(); Thread.Sleep(1); } }
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
