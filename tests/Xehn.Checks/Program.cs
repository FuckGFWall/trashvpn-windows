using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        // Existing geometry checks use the Chinese UI. The locale checks
        // below exercise other languages independently of the test computer.
        System.Globalization.CultureInfo.CurrentUICulture = new("zh-CN");
        Check(ClientSettings.Speed(null) == "" && ClientSettings.Speed(0) == "0 KB/s" && ClientSettings.Speed(double.NaN) == "", "measured zero shows 0 KB/s; unavailable rates remain blank");
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
        IconChecks.Run(Check, outDir);
        WorkflowChecks.Run(Check, outDir);
        NodeExportChecks.Run(Check);
        DashboardChecks.Run(Check);
        PreparationChecks.Run(Check);
        LiveFeedbackChecks.Run(Check);
        ModeSwitchChecks.Run(Check);
        NavigationChecks.Run(Check);
        ContentSizingChecks.Run(Check);
        WaterPowerChecks.Run(Check, outDir);
        ResourceFeedbackChecks.Run(Check, outDir);
        AppUpdateChecks.Run(Check, outDir);
        InstallAndLocaleChecks.Run(Check, outDir);
        WindowPresentationChecks.Run(Check);
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        double viewWidth = 800, viewHeight = 640;
        void Layout() { app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); root.Measure(new Size(viewWidth, viewHeight)); root.Arrange(new Rect(0, 0, viewWidth, viewHeight)); root.UpdateLayout(); }
        void Snapshot(string name)
        {
            // This visual tree is deliberately not a shown client window, so
            // explicitly render the controllable clock's current chart frame.
            foreach (var chart in Find<TrafficChart>(root)) chart.InvalidateVisual();
            foreach (var water in Find<WaterPowerSurface>(root)) water.RefreshFrame();
            Layout();
            var bitmap = new RenderTargetBitmap((int)viewWidth, (int)viewHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(outDir, name + ".png")); png.Save(stream);
        }
        Layout(); Layout();
        var vm = (MainViewModel)window.DataContext;
        Check(vm.Upload == "" && vm.Download == "", "initial UI has no fake traffic");
        Check(((FrameworkElement)window.FindName("PageHeader")).Visibility == Visibility.Collapsed, "home header removed");
        Check(window.FindName("PageCaption") == null && !Find<TextBlock>(root).Any(t => t.Text == "WARP, SIMPLIFIED."), "decorative subtitles removed");
        Check(window.Width == 800 && window.MinWidth == 800 && window.MaxWidth == 800 && window.Height == 680 && window.MinHeight == 680 && window.MaxHeight == 680 && window.ResizeMode == ResizeMode.CanMinimize && ((Grid)root).ColumnDefinitions[0].ActualWidth == 116, "800 by 680 fixed-size window preserves the compact rail without interactive resizing");
        Check(((TextBlock)window.FindName("PageTitle")).FontSize == 24 && window.FontSize == 14, "Settings / About / Logs headings shrink while body font stays unchanged");
        Check(((RadioButton)window.FindName("HomeNav")).ActualHeight == 44 && ((RadioButton)window.FindName("HomeNav")).ActualWidth is >= 90 and <= 92, "compact sidebar selection background");
        var brand = (FrameworkElement)window.FindName("BrandGroup"); var rail = (Border)window.FindName("Sidebar");
        Check(Math.Abs(brand.TranslatePoint(new Point(brand.ActualWidth / 2, 0), rail).X - (rail.ActualWidth - 1) / 2) < 1, "large icon is centered in the narrower rail");
        var navs = Find<RadioButton>(root).Where(b => b.GroupName == "Navigation").ToArray();
        Check(navs.All(n => ReferenceEquals(n.FocusVisualStyle, app.FindResource("NavKeyboardFocus")) && n.Focusable), "sidebar replaces dashed adorners with a solid keyboard-only focus treatment without disabling keyboard focus");
        Check(ReferenceEquals(rail.Background, app.FindResource("NavigationPaneBrush")) && rail.BorderThickness == new Thickness(0) && window.FindName("NavigationIndicator") is Border { Width: 3, Height: 16, IsHitTestVisible: false }, "sidebar uses a neutral Fluent pane and one unobtrusive accent selection pill");
        var connectionStatus = (StackPanel)window.FindName("ConnectionStatus");
        var dot = (StatusDot)window.FindName("ConnectionDot");
        Check(connectionStatus.Children.Count == 3 && connectionStatus.Children[1] is TextBlock { FontSize: 12 } && connectionStatus.Children[2] is ProgressRing && !Find<TextBlock>(rail).Any(t => t.Text.Contains("1.0.0")), "sidebar footer contains status dot, text and activity ring without a version");
        Check(connectionStatus.HorizontalAlignment == HorizontalAlignment.Center && Math.Abs(connectionStatus.TranslatePoint(new Point(connectionStatus.ActualWidth / 2, 0), rail).X - (rail.ActualWidth - 1) / 2) < 1 && !dot.IsConnected && !dot.IsChecking && ReferenceEquals(dot.IdleBrush, app.FindResource("MutedBrush")), "disconnected footer is centered with a grey status dot");
        Check(((Image)window.FindName("BrandIcon")).Width == 56 && !Find<TextBlock>((FrameworkElement)brand).Any(), "sidebar brand shows only the larger icon, not a software name");
        Check(Find<NavigationGlyph>((RadioButton)window.FindName("HomeNav")).Single().FontSize == 20 && Find<TextBlock>((RadioButton)window.FindName("HomeNav")).Single().FontSize == 16, "sidebar navigation icons and labels retain their compact font sizes");
        foreach (var nav in navs)
        {
            var icon = Find<NavigationGlyph>(nav).Single(); var label = Find<TextBlock>(nav).Single();
            Geometry TextOutline(string text, FontFamily family, double size, FontWeight weight) => new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, VisualTreeHelper.GetDpi(nav).PixelsPerDip).BuildGeometry(new Point());
            var original = TextOutline(icon.Text, icon.FontFamily, icon.FontSize, FontWeights.Normal);
            Check(icon.OutlineGeometry.GetArea() < original.GetArea() * .95 && icon.OutlineGeometry.GetArea() > original.GetArea() * .5 && !icon.Focusable && !icon.IsHitTestVisible, nav.Tag + " preserves the Fluent symbol with genuinely thinner contours and no extra focus/click target");
            Check(icon.IsSelected == (nav.IsChecked == true) && ReferenceEquals(icon.DrawingBrush, app.FindResource(icon.IsSelected ? "Accent" : "InkBrush")) && icon.FilledGeometry.GetArea() > icon.OutlineGeometry.GetArea() * 1.2, nav.Tag + " binds its orange solid variant to actual navigation selection, not merely thicker colored outlines");
            Check(label.FontWeight == FontWeights.Light && TextOutline(label.Text, label.FontFamily, label.FontSize, label.FontWeight).GetArea() < TextOutline(label.Text, label.FontFamily, label.FontSize, FontWeights.Normal).GetArea(), nav.Tag + " label renders lighter strokes at the same size, not merely a paler color");
            var shift = (string)nav.Tag is "About" or "Logs" ? 2 : 0;
            Check(icon.Margin == new Thickness(0, shift, 10, -shift) && Math.Abs(icon.TranslatePoint(new Point(), nav).Y - (nav.ActualHeight - icon.ActualHeight) / 2 - shift) <= .6 && Math.Abs(label.TranslatePoint(new Point(0, label.ActualHeight / 2), nav).Y - nav.ActualHeight / 2) <= .6, nav.Tag + " applies only its intended icon baseline correction without shifting labels or navigation rows");
        }
        Check(Find<ProgressBar>((FrameworkElement)window.FindName("HomePage")).Count() == 0 && Find<PreparationTrack>((FrameworkElement)window.FindName("HomePage")).Count() == 1, "home combines registration and selection into one thick timeline");
        Check(ReferenceEquals(window.Icon, Branding.Image) && ReferenceEquals(((Image)window.FindName("BrandIcon")).Source, Branding.Image), "window and sidebar render original upstream icon");
        Check(((FrameworkElement)window.FindName("ModeSelectors")).GetBindingExpression(UIElement.IsEnabledProperty)?.ParentBinding.Path.Path == "CanChangeModes", "mode selector binding permits connected hot switching");
        var home = (FrameworkElement)window.FindName("HomePage"); var viewport = (FrameworkElement)window.FindName("PageContent");
        Check(Find<FrameworkElement>(home).All(e => e.ToolTip == null), "home controls have no hover reminders, including split routing and power");
        bool HasScrollAncestor(DependencyObject node) { for (var p = VisualTreeHelper.GetParent(node); p != null; p = VisualTreeHelper.GetParent(p)) if (p is ScrollViewer) return true; return false; }
        Check(!HasScrollAncestor(home) && ((ScrollViewer)window.FindName("PageScroller")).Visibility == Visibility.Collapsed, "home is outside the scrolling region, not just hiding its scrollbar");
        Check(((FrameworkElement)window.FindName("HomeViewport")).ActualHeight <= viewport.ActualHeight && !Find<Viewbox>(root).Any(v => ReferenceEquals(v.Child, home)), "home fills the fixed client viewport without page-wide scaling or scrolling");
        foreach (var name in new[] { "IpCard", "RegionCard", "DataCenterCard", "LatencyCard" })
        {
            var card = (Border)window.FindName(name); var texts = Find<TextBlock>(card).ToArray();
            Check(card.ActualHeight == 40 && texts.Length == 2 && Math.Abs(texts[0].TranslatePoint(new Point(0, texts[0].ActualHeight / 2), card).Y - texts[1].TranslatePoint(new Point(0, texts[1].ActualHeight / 2), card).Y) < 1, name + " is a flat single-line label / value card");
        }
        Check(Find<TextBlock>((Border)window.FindName("RegionCard")).First().Text == "国家", "country information label contains only 国家");
        Check(Find<TextBlock>((Border)window.FindName("DataCenterCard")).First().Text == "数据中心" && Grid.GetColumn((Border)window.FindName("DataCenterCard")) == 2 && Grid.GetColumn((Border)window.FindName("LatencyCard")) == 3, "data-center card sits between country and latency without a second information row");
        var pills = new[] { "IpCard", "RegionCard", "DataCenterCard", "LatencyCard" }.Select(n => (Border)window.FindName(n)).ToArray();
        Check(Math.Abs(pills[0].ActualWidth - 161) < 1 && Enumerable.Range(1, 3).All(i => Math.Abs(pills[i].TranslatePoint(new Point(), root).X - pills[i - 1].TranslatePoint(new Point(pills[i - 1].ActualWidth, 0), root).X - 18) < 1), "IP capsule is narrower while all three inter-capsule gaps grow uniformly to 18 DIP");
        var trafficCard = (FrameworkElement)window.FindName("TrafficCard");
        Check(viewport.ActualHeight - trafficCard.TranslatePoint(new Point(0, trafficCard.ActualHeight), viewport).Y < 2 && ((FrameworkElement)window.FindName("ConnectionCard")).ActualHeight >= 378, "home gives reclaimed info-row height to connection card rather than leaving a bottom gap");
        var prep = (FrameworkElement)window.FindName("PreparationPanel"); var modes = (FrameworkElement)window.FindName("ModeSelectors");
        Check(modes.ActualWidth == 440 && Find<ModeSwitch>(modes).Count() == 2 && Find<ModeSwitch>(modes).All(s => s.ActualHeight == 46 && s.ActualWidth == 208), "both mode selectors retain their intended sizes at the preferred window dimensions");
        Check(Find<RadioButton>(modes).All(c => c.FontSize == 14), "proxy and routing selector labels are larger without changing selector size");
        Check(modes.TranslatePoint(new Point(), home).Y - prep.TranslatePoint(new Point(0, prep.ActualHeight), home).Y >= 40, "mode sliders leave more space below the preparation progress area");
        var power = (Button)window.FindName("PowerButton");
        Check(power.ActualWidth == 116 && ((Viewbox)power.Template.FindName("PowerGlyph", power)).Width == 54, "smaller power button preserves the original white power glyph size");
        Check(window.FindName("PowerVisual") is WaterPowerSurface idleWater && idleWater.State == PowerButtonState.Idle && ReferenceEquals(idleWater.IdleBrush, app.FindResource("PowerIdleBrush")) && power.Template.FindName("PowerProgressRing", power) == null, "home uses a themed gray idle water surface and never includes a large spinner");
        Snapshot("home-light");
        viewWidth = 800; viewHeight = 560; vm.Notice = string.Concat(Enumerable.Repeat("较长的连接提示。", 16)); Layout();
        Check(((FrameworkElement)window.FindName("TrafficCard")).TranslatePoint(new Point(0, ((FrameworkElement)window.FindName("TrafficCard")).ActualHeight), viewport).Y <= viewport.ActualHeight + .1, "minimum window with a long notice still fits every home card");
        var fittedCard = (Border)window.FindName("ConnectionCard");
        var fittedBounds = fittedCard.TransformToAncestor(root).TransformBounds(new Rect(0, 0, fittedCard.ActualWidth, fittedCard.ActualHeight));
        Check(Math.Abs(fittedBounds.Width - fittedCard.ActualWidth) < .1 && Math.Abs(fittedBounds.X - 144) < .1 && ((Border)window.FindName("TrafficCard")).TranslatePoint(new Point(), root).X == fittedBounds.X, "smaller fitted viewports and long notices cannot zoom home cards or change the common left gutter");
        Snapshot("home-minimum"); viewWidth = 800; viewHeight = 640; vm.Notice = ""; Layout();
        viewHeight = 760; Layout(); Layout();
        Check(viewport.ActualHeight - trafficCard.TranslatePoint(new Point(0, trafficCard.ActualHeight), viewport).Y < 2 && ((FrameworkElement)window.FindName("ConnectionCard")).ActualHeight > 500, "taller window expands the connection area without a blank strip below traffic");
        Snapshot("home-tall"); viewHeight = 640; Layout(); Layout();
        foreach (var page in new[] { "Settings", "About", "Logs", "Home" })
        {
            var nav = Find<RadioButton>(root).Single(b => (string?)b.Tag == page);
            nav.IsChecked = true; Layout();
            Check(((FrameworkElement)window.FindName(page + "Page")).Visibility == Visibility.Visible, page + " navigation");
            if (page != "Home") Check(((TextBlock)window.FindName("PageTitle")).FontSize == 24, page + " page heading is smaller without shrinking body text");
            Snapshot(page.ToLowerInvariant() + "-light");
            if (page == "Settings")
            {
                var panel = (FrameworkElement)window.FindName("SettingsPage");
                Check(((CheckBox)window.FindName("CloseToTraySetting")).IsChecked == true && window.FindName("AutoStartSetting") is CheckBox, "settings expose default-on tray closing and autostart");
                Check(Find<ComboBox>(panel).All(c => c is ContentComboBox fit && c.ActualHeight == 28 && Math.Abs(c.ActualWidth - fit.SelectionWidth) <= 1), "every settings selector measures only its current selected label with balanced padding");
                var exportRow = (Grid)window.FindName("NodeExportRow");
                var exportFormats = (ComboBox)window.FindName("ExportFormatPicker");
                Check(exportFormats.Items.Count == vm.NodeExport.Formats.Count && exportRow.TranslatePoint(new Point(), panel).Y > ((ComboBox)window.FindName("ProtocolPicker")).TranslatePoint(new Point(), panel).Y, "filtered export format and copy controls are directly below tunnel protocol");
                NodeExportChecks.PreviewCopied(window, () => Snapshot("settings-node-copied"), Check);
                var copyButton = (Button)window.FindName("CopyNodeButton");
                Check(double.IsNaN(copyButton.Width) && copyButton.ActualWidth < 64 && copyButton.ActualWidth >= 40, "node copy button fits its normal text and padding instead of reserving a fixed 64-DIP width");
                Check(Find<TextBox>(panel).All(t => t.ActualHeight == 28 && Math.Abs(t.ActualWidth - 56) < .1), "all inputs including custom DNS share the narrower 56-DIP width without shrinking fonts");
                var protocols = Find<ComboBox>(panel).Single(c => c.Items.OfType<ComboBoxItem>().Any(i => (string?)i.Tag == "masque-h2"));
                var selectedText = Find<TextBlock>(protocols).Single(t => t.TextTrimming == TextTrimming.CharacterEllipsis);
                var longestProtocol = protocols.Items.OfType<ComboBoxItem>().Max(i => new FormattedText((string)i.Content, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(selectedText.FontFamily, selectedText.FontStyle, selectedText.FontWeight, selectedText.FontStretch), selectedText.FontSize, selectedText.Foreground, VisualTreeHelper.GetDpi(selectedText).PixelsPerDip).WidthIncludingTrailingWhitespace);
                var currentProtocol = new FormattedText(selectedText.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(selectedText.FontFamily, selectedText.FontStyle, selectedText.FontWeight, selectedText.FontStretch), selectedText.FontSize, selectedText.Foreground, VisualTreeHelper.GetDpi(selectedText).PixelsPerDip).WidthIncludingTrailingWhitespace;
                Check(currentProtocol <= selectedText.ActualWidth + 1 && protocols.ActualWidth < longestProtocol + 40, "closed protocol selector fits its current label instead of reserving longest-option space");
                Check(window.FindName("ScanIntensityPicker") is ScanIntensitySlider picker && picker.GetBindingExpression(ScanIntensitySlider.IntensityProperty)?.ParentBinding.Path.Path == "ScanIntensity" && !Find<ComboBox>(panel).Any(c => c.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.ParentBinding.Path.Path == "ScanIntensity"), "scan intensity binds the existing setting through a draggable three-stop slider rather than a dropdown");
                Check((string?)((ComboBoxItem)protocols.Items[^1]).Tag == "masque-h2" && protocols.Items.Count == 6, "HTTP/2 is last while all six protocol identities remain available");
                Check(Find<TextBlock>(protocols).Single(t => t.TextTrimming == TextTrimming.CharacterEllipsis).FontSize == 13 && Equals(protocols.ToolTip, ((ComboBoxItem)protocols.SelectedItem).Content), "narrow protocol selector uses ellipsis at the unchanged font size and keeps its full label available on hover");
                Check(Find<ComboBox>(panel).All(c => c.FontSize == 13) && Find<TextBox>(panel).All(t => t.FontSize == 13) && Find<CheckBox>(panel).All(c => c.FontSize == 13) && Find<TextBlock>(panel).Single(t => t.Text == "自定义 DNS").FontSize == 14, "settings text sizes stay unchanged while control boxes shrink");
                Check(!Find<TextBlock>(panel).Any(t => t.Text.Contains("断开后修改")), "obsolete network-settings footer removed");
                var captions = Find<Border>(panel).Where(b => b.Style == app.FindResource("SettingsCard")).Select(b => Find<TextBlock>(b).First().Text).ToArray();
                Check(Array.IndexOf(captions, "外观") >= 0 && Array.IndexOf(captions, "连接行为") == Array.IndexOf(captions, "外观") + 1, "connection behavior is directly below appearance");
                Check(!Find<CheckBox>(panel).Any(c => c.Content?.ToString() is "启动后自动连接" or "连接异常时自动修复"), "startup auto-connect and optional repair switches are removed");
                var dnsHint = (TextBlock)window.FindName("DefaultDnsHint");
                var dnsInput = (TextBox)window.FindName("CustomDnsInput");
                Check(dnsHint.Text.Contains("1.1.1.1") && dnsHint.FontSize == 12 && dnsHint.Foreground == app.FindResource("MutedBrush") && dnsHint.TranslatePoint(new Point(), panel).Y >= dnsInput.TranslatePoint(new Point(0, dnsInput.ActualHeight), panel).Y, "small muted DNS default note sits below the custom DNS input row");
                var checkbox = (CheckBox)window.FindName("CloseToTraySetting");
                var tick = (System.Windows.Shapes.Path)checkbox.Template.FindName("Tick", checkbox);
                var box = (Border)checkbox.Template.FindName("Box", checkbox);
                var tickPosition = tick.TranslatePoint(new Point(), checkbox);
                for (var toggle = 0; toggle < 4; toggle++) { checkbox.IsChecked = false; Layout(); checkbox.IsChecked = true; Layout(); }
                var canvas = (Canvas)tick.Parent;
                Check(tick.TranslatePoint(new Point(), checkbox) == tickPosition && canvas.Width == 12 && canvas.HorizontalAlignment == HorizontalAlignment.Center && canvas.VerticalAlignment == VerticalAlignment.Center && box.BorderThickness == new Thickness(1), "checkbox tick remains centered after repeated uncheck / check cycles");
                Check(!checkbox.Template.Triggers.OfType<Trigger>().Where(t => t.Property == UIElement.IsKeyboardFocusedProperty).SelectMany(t => t.Setters.OfType<Setter>()).Any(s => s.Property == Border.BorderThicknessProperty), "checkbox focus cannot change the tick's layout or border thickness");
                var scroller = (ScrollViewer)window.FindName("PageScroller");
                var bar = (System.Windows.Controls.Primitives.ScrollBar)scroller.Template.FindName("PART_VerticalScrollBar", scroller);
                var card = Find<Border>(panel).First();
                var barLeft = bar.TranslatePoint(new Point(), root).X;
                Check(bar.Visibility == Visibility.Visible && barLeft >= viewWidth - 18 && card.TranslatePoint(new Point(card.ActualWidth, 0), root).X <= barLeft - 10, "page scrollbar sits at the far right outside the white card with a separate gutter");
                scroller.ScrollToBottom(); Layout(); Check(scroller.VerticalOffset > 0, "settings can still scroll through the full page");
                navs.Single(n => (string?)n.Tag == "Home").IsChecked = true; Layout(); nav.IsChecked = true; Layout();
                Check(scroller.VerticalOffset == 0, "returning to settings always starts at the top");
                scroller.ScrollToBottom(); Snapshot("settings-bottom"); scroller.ScrollToTop(); Layout();
                Check(!Find<TextBlock>(panel).Any(t=>t.Text=="API 端口") && !Find<CheckBox>(panel).Any(c=>c.Content?.ToString()=="每次连接重新扫描") && window.FindName("AutoConnectAfterPrepareSetting") is CheckBox, "settings remove controller port and every-connect rescan, adding post-selection auto-connect");
                using(var preferenceModel=new MainViewModel(new DashboardPreviewEngine(Path.Combine(outDir,"settings-preferences",Guid.NewGuid().ToString("N"))),lanAddressProvider:()=>["192.168.1.20"]))
                {
                    window.DataContext=preferenceModel; Layout();
                    var lan=(TextBlock)window.FindName("LanProxyEndpoints"); var days=(TextBox)window.FindName("CacheDaysInput"); var dayRow=(Grid)window.FindName("CacheDaysRow");
                    Check(lan.Visibility==Visibility.Collapsed && days.IsEnabled && days.Text=="1", "LAN details start hidden and cache input starts enabled at one day");
                    preferenceModel.AllowLan=true;
                    preferenceModel.MixedPortText="1080"; preferenceModel.CommitSettingsInput(nameof(MainViewModel.MixedPortText)); Layout();
                    Check(lan.Visibility==Visibility.Visible && lan.Text=="192.168.1.20:1080" && lan.TranslatePoint(new Point(),panel).Y>((CheckBox)window.FindName("AllowLanSetting")).TranslatePoint(new Point(),panel).Y, "actual LAN address and mixed-port row appears directly below its switch");
                    preferenceModel.CacheNeverExpires=true; Layout();
                    Check(!dayRow.IsEnabled && !days.IsEnabled && dayRow.Opacity==.4 && days.Text=="1", "never-expire switch greys the entire day row without clearing the saved value");
                    scroller.ScrollToBottom(); Layout(); Snapshot("settings-lan-cache-never");
                    preferenceModel.CacheNeverExpires=false; preferenceModel.AllowLan=false; Layout();
                    Check(days.IsEnabled && lan.Visibility==Visibility.Collapsed && lan.Text=="", "finite-cache and LAN toggles restore input and remove hidden address content");
                    var keyboard = Keyboard.PrimaryDevice;
                    var source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
                    bool PressEnter(TextBox input)
                    {
                        var e = new KeyEventArgs(keyboard, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        input.RaiseEvent(e);
                        return e.Handled;
                    }
                    days.SetCurrentValue(TextBox.TextProperty, "14");
                    preferenceModel.RefreshAsync().GetAwaiter().GetResult();
                    Layout();
                    Check(days.Text == "14" && ClientSettings.Load(preferenceModel.Home).CacheDays == 1, "typing only updates the cache lifetime draft, even across a status refresh");
                    Check(PressEnter(days), "cache lifetime input handles Enter in place");
                    Layout();
                    Check(days.Text == "14" && ClientSettings.Load(preferenceModel.Home).CacheDays == 14, "Enter persists the custom cache lifetime instead of reverting");
                    Snapshot("settings-cache-days");
                    days.SetCurrentValue(TextBox.TextProperty, "36501"); PressEnter(days); Layout();
                    Check(days.Text == "36501" && ClientSettings.Load(preferenceModel.Home).CacheDays == 14 && preferenceModel.Notice == ClientSettings.InvalidCacheDays, "an out-of-range edit stays available for correction without overwriting the last valid setting");
                    days.SetCurrentValue(TextBox.TextProperty, " 21 ");
                    days.RaiseEvent(new KeyboardFocusChangedEventArgs(keyboard, Environment.TickCount, days, nav) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                    Layout();
                    Check(days.Text == "21" && ClientSettings.Load(preferenceModel.Home).CacheDays == 21 && preferenceModel.Notice == "", "leaving the field saves valid input and clears its error");
                    foreach (var (input, draft, saved) in new (string, string, Func<ClientSettings, bool>)[]
                    {
                        ("TimeoutInput", "5", s => s.TimeoutSec == 5),
                        ("MixedPortInput", "9090", s => s.MixedPort == 9090 && s.ApiPort != 9090),
                        ("MtuInput", "1280", s => s.Mtu == 1280),
                        ("CustomDnsInput", "1.0.0.1, 8.8.8.8", s => s.DnsCustom == "1.0.0.1, 8.8.8.8"),
                    })
                    {
                        var editor = (TextBox)window.FindName(input);
                        editor.SetCurrentValue(TextBox.TextProperty, draft);
                        Check(!saved(ClientSettings.Load(preferenceModel.Home)), $"{input} keeps uncommitted edits out of settings");
                        var handled = PressEnter(editor); Layout();
                        Check(handled && editor.Text == draft && saved(ClientSettings.Load(preferenceModel.Home)), $"{input} persists its edited value on Enter");
                    }
                    var portInput = (TextBox)window.FindName("MixedPortInput");
                    portInput.SetCurrentValue(TextBox.TextProperty, "99999"); PressEnter(portInput); Layout();
                    Check(preferenceModel.Notice == ClientSettings.InvalidPort && ClientSettings.Load(preferenceModel.Home).MixedPort == 9090, "invalid port is not applied");
                    days.SetCurrentValue(TextBox.TextProperty, "9"); PressEnter(days); Layout();
                    Check(ClientSettings.Load(preferenceModel.Home).CacheDays == 9, "an invalid draft in another field does not block saving a valid input");
                }
                window.DataContext=vm; scroller.ScrollToTop(); Layout();
                App.ApplyTheme("dark"); Snapshot("settings-dark");
                App.ApplyTheme("light"); Layout();
            }
            if (page == "About")
            {
                var about = (Grid)window.FindName("AboutPage");
                Check(Math.Abs(((Border)window.FindName("ConnectionCard")).TranslatePoint(new Point(), root).X - ((Border)window.FindName("ProductCard")).TranslatePoint(new Point(), root).X) < .1, "home and About card left borders share the same sidebar gutter without a scaling offset");
                Check(about.RowDefinitions.Count == 2 && about.ColumnDefinitions.Count == 2 && about.Children.OfType<Border>().Count() == 4, "about page is a four-card 2x2 layout");
                Check(((TextBlock)window.FindName("ProductVersion")).Text == "v1.0.0", "about page prefixes app version with v");
                var titles = about.Children.OfType<Border>().Select(c => Find<TextBlock>(c).First()).ToArray();
                Check(titles.All(t => t.FontSize == 20 && t.FontWeight == FontWeights.SemiBold && t.FontFamily.Equals(titles[0].FontFamily)) && ((TextBlock)window.FindName("ProductVersion")).FontSize == 14, "all four About card headings use the same font and weight, with regular 14-DIP body text");
                Check(((TextBlock)window.FindName("CoreVersionText")).FontSize == 14 && ((TextBlock)window.FindName("ProductVersion")).FontSize == 14, "core version and unread/checking states use the same 14-DIP body size as app version, GeoFiles date and credits links");
                Check(about.Children.OfType<Border>().All(c => c.Padding == new Thickness(16, 10, 16, 10) && c.MinHeight == (c.Name is "CoreCard" or "GeoCard" ? 172 : 156) && c.ActualWidth <= 314) && about.ActualHeight < 360, "About cards are further narrowed and shorter, without large empty padding");
                Check(about.Children.OfType<Border>().All(c => c.CornerRadius == new CornerRadius(8) && ReferenceEquals(c.Background, app.FindResource("AboutSurfaceBrush")) && ReferenceEquals(c.BorderBrush, app.FindResource("ShellStrokeBrush"))), "all four About cards use consistent Windows 11 corners and subtle themed strokes");
                Check(Find<TextBlock>(about).Count(t => ReferenceEquals(t.Style, app.FindResource("AboutIcon"))) == 3 && Find<Image>((Border)window.FindName("ProductCard")).Single().Source == Branding.Image, "About headers pair Fluent component, globe and library glyphs with unchanged app branding");
                var footers = Find<Border>(about).Where(b => ReferenceEquals(b.Style, app.FindResource("AboutActionSurface"))).ToArray();
                Check(footers.Length == 4 && footers.All(f => ReferenceEquals(f.Background, app.FindResource("AboutFooterBrush"))) && footers.All(f => Math.Abs(f.ActualHeight - footers[0].ActualHeight) < 1), "product, resources and license actions share aligned, subtly separated native-style footer surfaces");
                Check(footers.All(f => f.ActualHeight <= 44) && Find<Button>(about).All(b => b.Height == 26 && b.FontSize == 12 && (b.Visibility != Visibility.Visible || b.ActualHeight == 26)), "About action bands are shorter with lower dividers and compact 26-DIP buttons at 12-DIP text");
                Check(Math.Abs(((Border)window.FindName("ProductCard")).TranslatePoint(new Point(), root).X - ((TextBlock)window.FindName("PageTitle")).TranslatePoint(new Point(), root).X) < 1 && Math.Abs(about.ActualWidth - 628) < 1, "About cards fill the fixed-width content column and align with the page heading");
                Check(window.FindName("CoreDownloadBar") is ProgressBar && window.FindName("GeoDownloadBar") is ProgressBar && window.FindName("CoreCheckButton") is ResourceFeedbackButton && window.FindName("GeoCheckButton") is ResourceFeedbackButton && window.FindName("CoreResourceStatus") == null && window.FindName("GeoResourceStatus") == null, "resource cards retain download bars and move success and checking feedback inside their buttons");
                foreach (var footer in footers)
                {
                    var card = about.Children.OfType<Border>().Single(c => Find<Border>(c).Contains(footer));
                    var button = Find<Button>(card).First();
                    Check(Math.Abs(button.TranslatePoint(new Point(0, button.ActualHeight / 2), card).Y - footer.TranslatePoint(new Point(0, footer.ActualHeight / 2), card).Y) <= 1, card.Name + " action buttons are lowered onto the centerline of their compact footer band");
                }
                var project = Find<TextBlock>((Border)window.FindName("ProductCard")).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Hyperlink>()).Single();
                Check(project.NavigateUri.AbsoluteUri == "https://github.com/FuckGFWall/trashvpn-windows" && !new System.Windows.Documents.TextRange(project.ContentStart, project.ContentEnd).Text.Contains("https://"), "project hyperlink hides raw URL and targets this repository");
                var product = (Border)window.FindName("ProductCard"); var productTitle = Find<TextBlock>(product).First(); var version = (TextBlock)window.FindName("ProductVersion");
                Check(version.TranslatePoint(new Point(), product).Y >= productTitle.TranslatePoint(new Point(0, productTitle.ActualHeight), product).Y && ((TextBlock)window.FindName("ProjectLinkText")).TranslatePoint(new Point(), product).Y >= version.TranslatePoint(new Point(0, version.ActualHeight), product).Y, "product name, version and project link occupy separate consecutive rows");
                foreach (var card in about.Children.OfType<Border>())
                {
                    var left = card.Padding.Left + card.BorderThickness.Left;
                    var title = Find<TextBlock>(card).First();
                    bool IsButtonText(DependencyObject text) { for (var p = VisualTreeHelper.GetParent(text); p != null && p != card; p = VisualTreeHelper.GetParent(p)) if (p is System.Windows.Controls.Primitives.ButtonBase) return true; return false; }
                    var body = Find<TextBlock>(card).Where(t => t.FontFamily.Source != "Segoe Fluent Icons, Segoe MDL2 Assets" && !ReferenceEquals(t, title) && !string.IsNullOrEmpty(t.Text) && !IsButtonText(t)).ToArray();
                    Check(Math.Abs(title.TranslatePoint(new Point(), card).X - left - 34) < .01 && body.All(t => Math.Abs(t.TranslatePoint(new Point(), card).X - left) < .01), card.Name + " keeps only its first-line title beside the icon; all subsequent text starts at the card's left content edge");
                }
                var scroller = (ScrollViewer)window.FindName("PageScroller");
                about.MinHeight = scroller.ViewportHeight + 300; Layout(); scroller.ScrollToBottom(); Layout(); var wasScrolled = scroller.VerticalOffset > 0;
                navs.Single(n => (string?)n.Tag == "Home").IsChecked = true; Layout(); nav.IsChecked = true; Layout();
                Check(wasScrolled && scroller.VerticalOffset == 0, "returning to a scrollable About page also resets to the top");
                about.MinHeight = 0; Layout();
                var updateNav = (Button)window.FindName("AppUpdateNav");
                var updateCheck = (ResourceFeedbackButton)window.FindName("AppUpdateCheckButton");
                Check(Equals(updateCheck.Content, "检查") && updateCheck.Visibility == Visibility.Visible && window.FindName("AppUpdateDownloadButton") == null &&
                    Find<Button>((Border)window.FindName("ProductCard")).Single() == updateCheck, "XEHN's top-left About card has only the original check action");
                using (var available = AppUpdateChecks.PreviewAvailable(outDir))
                {
                    window.DataContext = available; Layout();
                    Check(Equals(updateCheck.Content, "下载") && updateNav.Visibility == Visibility.Visible &&
                        Equals(updateCheck.Tag, AppReleaseUpdate.DownloadPage) && Equals(updateNav.Tag, updateCheck.Tag) &&
                        Find<Button>((Border)window.FindName("ProductCard")).Single() == updateCheck, "a newer release changes the original Check button to Download in place, using the same URL as the sidebar");
                    var logsNav = navs.Single(n => Equals(n.Tag, "Logs"));
                    var navigation = (StackPanel)window.FindName("NavigationItems");
                    var fill = (Border)updateNav.Template.FindName("Fill", updateNav);
                    Check(navigation.Children.IndexOf(updateNav) == navigation.Children.IndexOf(logsNav) + 1 &&
                        updateNav.ActualHeight == 36 && updateNav.Content.Equals("有更新") && fill.Background is SolidColorBrush { Color.A: 255 } &&
                        Find<TextBlock>(fill).All(t => t.Foreground is SolidColorBrush brush && brush.Color == Colors.White) &&
                        ReferenceEquals(nav, navs.Single(n => n.IsChecked == true)), "update action follows Logs, has an opaque idle fill and does not take navigation selection");
                    Check(((TextBlock)window.FindName("AppUpdateStatus")).Text == "发现新版本 v1.2.0", "the About card shows the discovered Release tag");
                    Snapshot("about-app-update");
                    App.ApplyTheme("dark"); Snapshot("about-app-update-dark"); App.ApplyTheme("light");
                    available.Language = "en"; Layout();
                    Check(((TextBlock)window.FindName("PageTitle")).Text == "About" &&
                        window.FlowDirection == FlowDirection.LeftToRight && Equals(updateCheck.Content, "Download") &&
                        Equals(updateNav.Content, "Update") && ((TextBlock)window.FindName("AppUpdateStatus")).Text == "New version: v1.2.0",
                        "English immediately translates navigation, update action and dynamic release text");
                    Snapshot("about-update-en");
                    available.Language = "fa"; Layout();
                    Check(((TextBlock)window.FindName("PageTitle")).Text == "درباره" &&
                        window.FlowDirection == FlowDirection.RightToLeft && Equals(updateCheck.Content, "دریافت") &&
                        ((TextBlock)window.FindName("AppUpdateStatus")).Text == "نسخهٔ جدید: v1.2.0", "Persian translates the release UI and mirrors the shell");
                    Snapshot("about-update-fa");
                    navs.Single(n => Equals(n.Tag, "Settings")).IsChecked = true; Layout();
                    var language = (ContentComboBox)window.FindName("LanguagePicker");
                    Check(Equals(language.SelectedValue, "fa") && language.Items.Count == 3 && ((TextBlock)window.FindName("PageTitle")).Text == "تنظیمات",
                        "settings displays the selected Persian locale and all three choices");
                    Snapshot("settings-fa");
                    language.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "en"); Layout();
                    Check(available.Language == "en" && ((TextBlock)window.FindName("PageTitle")).Text == "Settings" &&
                        Find<TextBlock>((FrameworkElement)window.FindName("SettingsPage")).Any(t => t.Text == "Connection behavior"),
                        "selecting English in settings instantly re-translates the page");
                    Snapshot("settings-en");
                    available.Language = "zh";
                    nav.IsChecked = true; Layout();
                }
                window.DataContext = vm; Layout();
                App.ApplyTheme("dark"); Snapshot("about-dark"); App.ApplyTheme("light"); Layout();
            }
            if (page == "Logs")
            {
                var logText = (TextBox)window.FindName("LogText"); var scroller = (ScrollViewer)window.FindName("LogScroller");
                Check(logText.FontSize == 12 && Find<TextBlock>((FrameworkElement)window.FindName("LogsPage")).Single(t => t.Text == "运行日志").FontSize == 16, "Logs page retains its original text sizes");
                vm.Log(string.Join('\n', Enumerable.Range(1, 180).Select(i => $"isolated log fixture line {i}"))); Layout();
                var logs = (FrameworkElement)window.FindName("LogsPage"); var toolbar = (FrameworkElement)window.FindName("LogsToolbar");
                var toolbarPosition = toolbar.TranslatePoint(new Point(), root);
                Check(!HasScrollAncestor(logs) && ((ScrollViewer)window.FindName("PageScroller")).Visibility == Visibility.Collapsed && scroller.ScrollableHeight > 0, "log page outer frame is fixed; only the log body can scroll");
                var copy = (Button)window.FindName("CopyLogsButton");
                var title = Find<TextBlock>(toolbar).Single(t => t.Text == "运行日志");
                Check(copy.HorizontalAlignment == HorizontalAlignment.Right && Math.Abs(copy.TranslatePoint(new Point(0, copy.ActualHeight / 2), toolbar).Y - title.TranslatePoint(new Point(0, title.ActualHeight / 2), toolbar).Y) < 1 && scroller.ActualHeight > 450, "copy button sits right of run-log heading, leaving more height for actual logs");
                var logBar = (System.Windows.Controls.Primitives.ScrollBar)scroller.Template.FindName("PART_VerticalScrollBar", scroller);
                var logCard = (Border)window.FindName("LogsCard");
                var inset = logCard.TranslatePoint(new Point(logCard.ActualWidth, 0), root).X - logBar.TranslatePoint(new Point(logBar.ActualWidth, 0), root).X;
                Check(inset >= 6 && inset <= 12, "only the log body scrollbar sits just inside its white card's right border");
                var internalScroll = (ScrollViewer)logText.Template.FindName("PART_ContentHost", logText);
                Check(internalScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && internalScroll.ComputedVerticalScrollBarVisibility != Visibility.Visible && !Find<System.Windows.Controls.Primitives.ScrollBar>(logText).Any(b => b.Visibility == Visibility.Visible), "log TextBox internal scroll container cannot display a second scrollbar");
                logText.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent }); Layout();
                Check(scroller.VerticalOffset > 0 && toolbar.TranslatePoint(new Point(), root) == toolbarPosition, "mouse wheel scrolls log text without moving toolbar or copy button");
                var track = (System.Windows.Controls.Primitives.Track)logBar.Template.FindName("PART_Track", logBar);
                var beforeDrag = scroller.VerticalOffset;
                track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
                track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 35) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent }); Layout();
                track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 35, false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent }); Layout();
                Check(scroller.VerticalOffset > beforeDrag + 1 && toolbar.TranslatePoint(new Point(), root) == toolbarPosition, "log scrollbar thumb changes only the body offset, not the fixed header");
                Snapshot("logs-fixed-toolbar"); scroller.ScrollToBottom(); Layout();
                navs.Single(n => (string?)n.Tag == "About").IsChecked = true; Layout(); nav.IsChecked = true; Layout();
                Check(Math.Abs(scroller.VerticalOffset - scroller.ScrollableHeight) < 1, "returning to logs keeps the latest entries at the bottom");
                App.ApplyTheme("dark"); Snapshot("logs-dark"); App.ApplyTheme("light");
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
            Check(surface.CornerRadius.TopLeft == 10 && ReferenceEquals(menu.Background, app.FindResource("PageBrush")), "compact tray menu keeps a rounded themed surface instead of a legacy white strip");
            Check(menu.ActualWidth < 108 && menu.ActualHeight <= 90 && menu.FontSize == 11 && menu.Items.Cast<MenuItem>().All(i => i.ActualHeight == 26 && i.Margin == new Thickness(0)), "tray menu fits content at the smaller 11-DIP font with compact 26-DIP rows and no row gaps");
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
            Layout(); Check(dot.IsChecking && !dot.IsConnected, "node preparation binds the grey-orange status pulse even before a connect request");
            preview.Emit("@@XEHN {\"type\":\"progress\",\"phase\":\"register\",\"text\":\"注册 MASQUE\",\"done\":1,\"total\":2,\"step\":1,\"steps\":6,\"operation\":\"注册账号\",\"detail\":\"1/2 账号 · 注册 MASQUE\"}");
            Layout(); Snapshot("home-registering");
            preview.Emit("@@XEHN {\"type\":\"progress\",\"phase\":\"scan\",\"text\":\"验证隧道 · 完成 4/14 · 可用 2/2 · 跳过 10\\n正在准备并复用已有账号与优选结果\",\"done\":60,\"total\":100,\"step\":5,\"steps\":7,\"operation\":\"验证隧道\",\"detail\":\"4/14 节点 · 可用 2/2 · 跳过 10\"}");
            Layout();
            var preparationBar = (PreparationTrack)window.FindName("PreparationBar");
            var preparationCaption = (TextBlock)window.FindName("PreparationCaption");
            var pausePreparation = (Button)window.FindName("PausePreparationButton");
            var rescan = (Button)window.FindName("RescanButton");
            Check(preparationBar.Progress > 33 && preparationBar.RegistrationComplete && preparationBar.TrackThickness == 10 && preparationCaption.Text.StartsWith("5/7 验证隧道 · 4/14 节点"), "actual home binds one thick bar and structured major-step / candidate progress");
            Check(pausePreparation.IsEnabled && rescan.IsEnabled && pausePreparation.TranslatePoint(new Point(), prep).X > preparationCaption.TranslatePoint(new Point(), prep).X && rescan.TranslatePoint(new Point(), prep).Y < preparationBar.TranslatePoint(new Point(), prep).Y, "pause and rescan sit above the bar on the right without covering step text");
            Snapshot("home-selecting");
            var pausedPreparation = model.TogglePreparationAsync();
            DashboardPreviewEngine.Wait(() => pausedPreparation.IsCompleted, Layout); pausedPreparation.GetAwaiter().GetResult(); Layout();
            Check(!preparationBar.IsActive && pausePreparation.Content?.ToString() == "继续" && preparationCaption.Text.StartsWith("已暂停"), "home pause freezes the bar and changes its actual action to continue");
            Snapshot("home-preparation-paused");
            model.TogglePreparationAsync().GetAwaiter().GetResult(); Layout();
            Layout(); var capture = (ModeSwitch)window.FindName("CaptureSwitch"); var routing = (ModeSwitch)window.FindName("RoutingSwitch");
            var capturePixels = ModeSwitchChecks.Pixels(capture); var routingPixels = ModeSwitchChecks.Pixels(routing);
            var connect = model.ToggleAsync(); Layout();
            Check(((TextBlock)window.FindName("ConnectionHeading")).Text == "正在检查" && ((TextBlock)window.FindName("SidebarStatusText")).Text == "正在检查" && dot.IsChecking, "queued connection keeps both headings in checking state while node selection runs");
            Check(capturePixels.SequenceEqual(ModeSwitchChecks.Pixels(capture)) && routingPixels.SequenceEqual(ModeSwitchChecks.Pixels(routing)), "VPN connect busy state preserves both selector backgrounds pixel-for-pixel");
            Snapshot("home-connecting");
            var glyph = (Viewbox)power.Template.FindName("PowerGlyph", power); var water = (WaterPowerSurface)window.FindName("PowerVisual");
            Check(glyph.Visibility == Visibility.Visible && power.Template.FindName("PowerProgressRing", power) == null && water.State == PowerButtonState.Connecting && ((ProgressRing)window.FindName("StatusProgressRing")).IsActive, "connection keeps white power lines over water, never a large spinner; small sidebar ring remains active");
            preview.Complete(); DashboardPreviewEngine.Wait(() => preview.Command == "desktop-connect", Layout);
            Check(preparationBar.IsReady && preparationBar.Progress == 100 && preparationCaption.Text == "缓存已就绪" && !pausePreparation.IsEnabled && !rescan.IsEnabled, "cache-ready home is green with no step numbers and no unsafe controls during core startup");
            Snapshot("home-cache-ready"); App.ApplyTheme("dark"); Layout(); Snapshot("home-cache-ready-dark"); App.ApplyTheme("light"); Layout();
            Layout(); Check(((TextBlock)window.FindName("ConnectionHeading")).Text == "正在连接" && ((TextBlock)window.FindName("SidebarStatusText")).Text == "正在连接" && !dot.IsChecking, "only actual core connection switches both headings from checking to connecting");
            preview.Connected = true; preview.Emit("@@XEHN {\"type\":\"connection\",\"state\":\"provisional\"}"); Layout();
            Check(((TextBlock)window.FindName("ConnectionHeading")).Text == "已连接" && water.State == PowerButtonState.Connected && glyph.Visibility == Visibility.Visible, "post-connection verification keeps connected wording and power lines while the finite success fill completes");
            DashboardPreviewEngine.Wait(() => { water.RefreshFrame(); return water.FillLevel == 1; }, Layout);
            Check(((TextBlock)window.FindName("SidebarStatusText")).Text == "已连接" && ((ProgressRing)window.FindName("StatusProgressRing")).IsActive && dot.IsConnected && !dot.IsChecking && ReferenceEquals(dot.ActiveBrush, app.FindResource("Accent")), "actual connected sidebar keeps its ring and orange dot without a checking suffix");
            Snapshot("home-verifying");
            preview.Emit("@@XEHN {\"type\":\"connection\",\"state\":\"verified\"}"); preview.Complete(); DashboardPreviewEngine.Wait(() => connect.IsCompleted, Layout); connect.GetAwaiter().GetResult(); Layout();
            Check(water.State == PowerButtonState.Connected && glyph.Visibility == Visibility.Visible, "verified connection retains the unchanged white power glyph and full orange fill");
            Snapshot("home-connected");
            var ipValue = Find<TextBlock>((Border)window.FindName("IpCard")).Last();
            Check(ipValue.Text == "203.0.113.42" && ipValue.ActualWidth > 90 && ipValue.TextWrapping == TextWrapping.NoWrap, "connected IPv4 fits beside its label on the single information row");
            Check(Find<TextBlock>((Border)window.FindName("RegionCard")).Last().Text == "新加坡" && Find<TextBlock>((Border)window.FindName("DataCenterCard")).Last().Text == "SIN", "country and CF colo are displayed independently in their actual cards");
            model.SplitSelected = true; Layout();
            Check(water.State == PowerButtonState.Connected && glyph.Visibility == Visibility.Visible && ((ProgressRing)window.FindName("StatusProgressRing")).IsActive, "starting hot routing switch keeps the completed power appearance and only spins the sidebar ring");
            foreach (var phase in new[] { "verifying", "provisional", "verified" })
            {
                preview.Emit("@@XEHN {\"type\":\"connection\",\"state\":\"" + phase + "\"}"); Layout();
                Check(water.State == PowerButtonState.Connected && glyph.Visibility == Visibility.Visible, "hot-switch " + phase + " event cannot replace the completed power appearance with a connection animation");
            }
            preview.Complete(); DashboardPreviewEngine.Wait(() => model.ModeChangeTask.IsCompleted, Layout); model.ModeChangeTask.GetAwaiter().GetResult(); Layout();
            Check(water.State == PowerButtonState.Connected && glyph.Visibility == Visibility.Visible, "finished hot switch does not flash a late power animation");
            Snapshot("home-hot-switch-finished");
            capturePixels = ModeSwitchChecks.Pixels(capture); routingPixels = ModeSwitchChecks.Pixels(routing);
            var disconnect = model.ToggleAsync(); Layout();
            Check(capturePixels.SequenceEqual(ModeSwitchChecks.Pixels(capture)) && routingPixels.SequenceEqual(ModeSwitchChecks.Pixels(routing)), "VPN disconnect busy state also preserves selector pixels without flashing");
            preview.Connected = false; preview.Complete(); DashboardPreviewEngine.Wait(() => disconnect.IsCompleted, Layout); disconnect.GetAwaiter().GetResult();
            Layout(); Check(!dot.IsConnected && !dot.IsChecking && ReferenceEquals(dot.IdleBrush, app.FindResource("MutedBrush")) && model.Region == "" && model.DataCenter == "", "disconnect returns the dot to grey and clears country / data center");
            DashboardPreviewEngine.Wait(() => { water.RefreshFrame(); return water.FillLevel == 0; }, Layout);
            Check(water.State == PowerButtonState.Idle && !water.IsAnimating && glyph.Visibility == Visibility.Visible, "disconnected home finishes its water drain with white power lines over a gray idle button");
            time.Advance(2); model.RefreshAsync().GetAwaiter().GetResult(); Snapshot("home-traffic-retained");
            time.Advance(12); Snapshot("home-traffic-aged");
            window.DataContext = vm;
        }
        using (var previewModel = new MainViewModel(new EngineService(Path.Combine(outDir, "resource-preview", Guid.NewGuid().ToString("N"))), resourceService: new PreviewResources()))
        {
            previewModel.InitializeResourcesAsync().GetAwaiter().GetResult();
            window.DataContext = previewModel;
            Find<RadioButton>(root).Single(b => (string?)b.Tag == "About").IsChecked = true;
            Layout();
            var initialGeoActions = (Grid)window.FindName("GeoActions");
            var initialGeoPosition = initialGeoActions.TranslatePoint(new Point(), root);
            PreviewResources.HoldCheck();
            var initialGeoCheck = previewModel.RunResourceActionAsync("resources-info"); Layout();
            Check(initialGeoActions.TranslatePoint(new Point(), root) == initialGeoPosition, "replacing a multi-line stale-GeoFiles message with checking text cannot move the button");
            PreviewResources.Complete(previewModel, initialGeoCheck, Layout); Layout();
            Check(initialGeoActions.TranslatePoint(new Point(), root) == initialGeoPosition, "restoring the stale-GeoFiles result also keeps its button anchored");
            var coreDownload = previewModel.RunResourceActionAsync("resources-download-core");
            Layout(); Snapshot("about-core-download-light");
            Check(((ProgressBar)window.FindName("CoreDownloadBar")).Visibility == Visibility.Visible && ((ProgressBar)window.FindName("CoreDownloadBar")).Value == 50, "core card renders live byte progress");
            PreviewResources.Emit(new("core", "downloaded", "mihomo.zip", 2097152, 2097152)); Layout();
            Check(((ProgressBar)window.FindName("CoreDownloadBar")).Visibility == Visibility.Collapsed && !coreDownload.IsCompleted, "actual core progress control disappears immediately at file EOF");
            PreviewResources.Emit(new("core", "validate", "", 0, 0)); Layout(); Snapshot("about-core-validating");
            Check(((ProgressBar)window.FindName("CoreDownloadBar")).Visibility == Visibility.Collapsed && previewModel.CoreResourceDetail.Contains("校验"), "core validation does not leave an indeterminate progress control behind");
            PreviewResources.Complete(previewModel, coreDownload, Layout);
            Layout();
            Check(((ResourceFeedbackButton)window.FindName("CoreCheckButton")).ShowSuccess && window.FindName("CoreDownloadButton") == null && previewModel.CoreResourceActionLabel == "检查", "core download completion uses the only original action button for its checkmark and returns its normal action to checking");
            var geoDownload = previewModel.RunResourceActionAsync("resources-download-geo");
            Layout(); Snapshot("about-geo-download-light");
            Check(((ProgressBar)window.FindName("GeoDownloadBar")).Visibility == Visibility.Visible && previewModel.CoreResourceSucceeded, "GeoFiles progress does not discard the completed core-download result");
            PreviewResources.Emit(new("geo", "downloaded", "geosite.dat", 0, 0)); Layout();
            Check(((ProgressBar)window.FindName("GeoDownloadBar")).Visibility == Visibility.Collapsed && !geoDownload.IsCompleted, "actual GeoFiles progress control disappears at unknown-size EOF");
            PreviewResources.Complete(previewModel, geoDownload, Layout);
            Layout();
            Check(((ResourceFeedbackButton)window.FindName("GeoCheckButton")).ShowSuccess && window.FindName("GeoDownloadButton") == null &&
                Find<Button>((Border)window.FindName("GeoCard")).Count() == 1 && Find<Button>((Border)window.FindName("CoreCard")).Count() == 1 &&
                previewModel.GeoResourceActionLabel == "检查", "GeoFiles replacement reports in the same sole action button; neither resource card has a right-side download button");
            Layout(); Check(((ProgressBar)window.FindName("GeoDownloadBar")).Visibility == Visibility.Collapsed && ((ProgressBar)window.FindName("CoreDownloadBar")).Visibility == Visibility.Collapsed, "completed resource cards retain confirmation text, not progress bars");
            Snapshot("about-resources-updated");
            previewModel.RunResourceActionAsync("resources-info").GetAwaiter().GetResult();
            Snapshot("about-geofiles-checked");
            Check(((ResourceFeedbackButton)window.FindName("GeoCheckButton")).ShowSuccess && previewModel.GeoResourceDetail == "", "successful GeoFiles inspection shows a transient green check inside its check button");
            foreach (var name in new[] { "Core", "Geo" })
            {
                var actions = (Grid)window.FindName(name + "Actions");
                previewModel.RunResourceActionAsync(name == "Core" ? "resources-check-core" : "resources-info").GetAwaiter().GetResult(); Layout();
                var check = (ResourceFeedbackButton)window.FindName(name + "CheckButton");
                var status = Find<System.Windows.Shapes.Path>(check).Single(p => p.Name == "FeedbackSuccess");
                Check(status.Visibility == Visibility.Visible && Math.Abs(status.TranslatePoint(new Point(status.ActualWidth / 2, status.ActualHeight / 2), check).Y - check.ActualHeight / 2) < 1 && Find<FrameworkElement>(check).Single(e => e.Name == "FeedbackLabel").Visibility == Visibility.Hidden && status.Stroke is SolidColorBrush success && success.Color.G > success.Color.R, name + " green success mark replaces text inside the actual clicked button");
                var card = (Border)window.FindName(name == "Core" ? "CoreCard" : "GeoCard");
                Check(card.ActualHeight <= 176 && actions.TranslatePoint(new Point(0, actions.ActualHeight), card).Y >= card.ActualHeight - 12, name + " action buttons sit near the bottom of the shorter card");
                var feedback = (FrameworkElement)window.FindName(name + "Feedback");
                Check(Grid.GetRow(feedback) < Grid.GetRow(actions) && feedback.TranslatePoint(new Point(0, feedback.ActualHeight), card).Y <= actions.TranslatePoint(new Point(), card).Y, name + " progress and detail region stays above the check button");
                Check(feedback.ActualHeight == 40, name + " reserves a fixed feedback region so changing line counts cannot move actions");
            }
            foreach (var name in new[] { "Core", "Geo" })
            {
                var actions = (Grid)window.FindName(name + "Actions");
                var card = (Border)window.FindName(name == "Core" ? "CoreCard" : "GeoCard");
                var before = actions.TranslatePoint(new Point(), card).Y;
                PreviewResources.HoldCheck();
                var checking = previewModel.RunResourceActionAsync(name == "Core" ? "resources-check-core" : "resources-info"); Layout();
                Check(Math.Abs(actions.TranslatePoint(new Point(), card).Y - before) < 1 && !checking.IsCompleted, name + " check button does not jump when working-status text appears");
                var checkingButton = (ResourceFeedbackButton)window.FindName(name + "CheckButton");
                Check(checkingButton.IsWorking && Find<ProgressRing>(checkingButton).Single().IsActive && (name == "Core" ? previewModel.CoreResourceDetail : previewModel.GeoResourceDetail) == "", name + " checking uses an in-button spinner without checking text above it");
                PreviewResources.Complete(previewModel, checking, Layout); Layout();
            }
            Check(previewModel.CoreVersion == "v1.19.33" && previewModel.CoreResourceDetail == "" && previewModel.Notice == "", "core versions have one v prefix and successful actions leave no persistent confirmation text");
            Find<RadioButton>(root).Single(b => (string?)b.Tag == "Home").IsChecked = true; Layout();
            Find<RadioButton>(root).Single(b => (string?)b.Tag == "About").IsChecked = true; Layout();
            Check(!((ResourceFeedbackButton)window.FindName("CoreCheckButton")).ShowSuccess && !((ResourceFeedbackButton)window.FindName("GeoCheckButton")).ShowSuccess && previewModel.CoreResourceDetail == "" && previewModel.GeoResourceDetail == "", "returning to About clears transient button checks without bringing back old success text");
            Snapshot("about-return-cleared");
            window.DataContext = vm;
        }
        if (args.Length > 1) IconChecks.CheckExecutable(Check, args[1]);
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
        public Task<Session> ReadSessionAsync() => Task.FromResult(new Session(Connected, "203.0.113.42", "新加坡", "SIN", 34));
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
        private Action<ResourceProgress>? report;
        private bool holdCheck;
        public PreviewResources() => active = this;
        public Task<ResourceInfo> RunAsync(string command, Action<string> log, Action<ResourceProgress>? progress = null)
        {
            this.command = command;
            report = progress;
            if (holdCheck && command is "resources-info" or "resources-check-core") { pending = new(TaskCreationOptions.RunContinuationsAsynchronously); return pending.Task; }
            if (command.Contains("download", StringComparison.Ordinal))
            {
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                progress?.Invoke(new(command.EndsWith("core") ? "core" : "geo", "download", command.EndsWith("core") ? "mihomo.zip" : "geosite.dat", 1048576, 2097152));
                return pending.Task;
            }
            if (command == "resources-apply") { current = current with { GeoPending = false, GeoNeedsDownload = false, GeoUpdated = "2026-10-30" }; progress?.Invoke(new("geo", "installed", "", 1, 1)); }
            return Task.FromResult(current);
        }
        public static void Emit(ResourceProgress progress) => active!.report?.Invoke(progress);
        public static void HoldCheck() => active!.holdCheck = true;
        public static void Complete(MainViewModel model, Task task, Action layout)
        {
            var self = active!;
            self.holdCheck = false;
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
