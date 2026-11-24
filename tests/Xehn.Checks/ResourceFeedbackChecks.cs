using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Xehn.App;

internal static class ResourceFeedbackChecks
{
    public static void Run(Action<bool, string> check, string outDir)
    {
        var button = new ResourceFeedbackButton { Content = "检查", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var fixture = new Window { Content = button, Width = 140, Height = 80, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            fixture.Show(); Pump();
            var label = Descendants<FrameworkElement>(button).Single(e => e.Name == "FeedbackLabel");
            var mark = Descendants<FrameworkElement>(button).Single(e => e.Name == "FeedbackSuccess");
            var ring = Descendants<ProgressRing>(button).Single(); var width = button.ActualWidth;
            check(label.Visibility == Visibility.Visible && mark.Visibility == Visibility.Collapsed && !ring.IsActive, "original check button initially displays only its normal label");
            button.IsEnabled = false; button.IsWorking = true; Pump();
            check(label.Visibility == Visibility.Hidden && ring.IsActive && ring.IsAnimating && mark.Visibility == Visibility.Collapsed && button.ActualWidth == width, "checking replaces the original check label with a live in-button spinner without changing bounds");
            button.IsSuccessful = true; Pump();
            check(!button.ShowSuccess && ring.IsActive, "an installed progress event cannot show success before the operation finishes");
            var elapsed = Stopwatch.StartNew(); button.IsWorking = false; button.IsEnabled = true; Pump();
            check(button.ShowSuccess && label.Visibility == Visibility.Hidden && mark.Visibility == Visibility.Visible && !ring.IsActive && button.ActualWidth == width, "completion replaces the label with one centered green check inside the same button");
            Thread.Sleep(150); Pump(); check(button.ShowSuccess, "success check is not cleared immediately after completion");
            Wait(() => !button.ShowSuccess);
            check(elapsed.Elapsed.TotalSeconds >= .95 && elapsed.Elapsed.TotalSeconds < 3 && label.Visibility == Visibility.Visible && mark.Visibility == Visibility.Collapsed && button.ActualWidth == width, "after one second the green check disappears and the original label returns at exactly the same width");
            button.IsSuccessful = false; button.IsWorking = true; Pump(); button.IsWorking = false; Pump();
            check(!button.ShowSuccess && label.Visibility == Visibility.Visible && !ring.IsActive, "failed checking restores text without a false success check");
            button.IsSuccessful = true; Pump(); button.Visibility = Visibility.Collapsed; Pump(); button.Visibility = Visibility.Visible; Pump();
            check(!button.ShowSuccess && label.Visibility == Visibility.Visible, "leaving and returning cannot replay an old successful check");
            button.IsSuccessful = false; button.IsWorking = true; Pump(); fixture.Close(); Pump();
            check(!ring.IsAnimating && !button.ShowSuccess, "closing releases both spinner animation and pending feedback timer");
        }
        finally { fixture.Close(); Pump(); }
        var resources = new ResourceFixture();
        using var model = new MainViewModel(new EngineService(Path.Combine(outDir, "single-resource", Guid.NewGuid().ToString("N"))), resourceService: resources);
        model.InitializeResourcesAsync().GetAwaiter().GetResult();
        check(model.CoreResourceActionLabel == "检查" && model.GeoResourceActionLabel == "检查" && resources.Downloads == 0, "each resource starts with one check action; background metadata never silently starts a manual download");
        model.RunResourceActionAsync(model.CoreResourceActionCommand).GetAwaiter().GetResult();
        check(model.CoreResourceActionLabel == "下载" && model.CoreResourceActionCommand == "resources-download-core" && !model.CoreResourceSucceeded, "a missing core changes that same action to explicit download, without adding a right-side button");
        model.RunResourceActionAsync(model.CoreResourceActionCommand).GetAwaiter().GetResult();
        check(model.CoreResourceActionLabel == "检查" && model.CoreResourceSucceeded && resources.Downloads == 1, "successful core replacement restores the same action to checking and publishes its in-button result");
        model.RunResourceActionAsync(model.GeoResourceActionCommand).GetAwaiter().GetResult();
        check(model.GeoResourceActionLabel == "下载" && model.GeoResourceActionCommand == "resources-download-geo", "outdated GeoFiles likewise reuse their original action for an explicit download");
        model.RunResourceActionAsync(model.GeoResourceActionCommand).GetAwaiter().GetResult();
        check(model.GeoResourceActionLabel == "检查" && model.GeoResourceSucceeded && resources.Downloads == 2, "successful GeoFiles replacement returns to one check action without a second feedback control");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value; foreach (var descendant in Descendants<T>(child)) yield return descendant; }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private static void Wait(Func<bool> ready) { var limit = Stopwatch.StartNew(); while (!ready()) { if (limit.Elapsed.TotalSeconds > 3) throw new TimeoutException("Resource feedback did not settle"); Thread.Sleep(5); Pump(); } }
    private sealed class ResourceFixture : IResourceService
    {
        private ResourceInfo info = new("", true, "未找到内核", "", true, false);
        public int Downloads;
        public Task<ResourceInfo> RunAsync(string command, Action<string> log, Action<ResourceProgress>? progress = null)
        {
            if (command == "resources-download-core") { Downloads++; info = info with { CoreVersion = "1.19.33", CoreNeedsDownload = false, CoreCheck = "全部协议检查通过" }; progress?.Invoke(new("core", "installed", "", 1, 1)); }
            if (command == "resources-download-geo") { Downloads++; info = info with { GeoNeedsDownload = false, GeoUpdated = "2026-11-17" }; progress?.Invoke(new("geo", "installed", "", 1, 1)); }
            return Task.FromResult(info);
        }
        public Task CancelAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
