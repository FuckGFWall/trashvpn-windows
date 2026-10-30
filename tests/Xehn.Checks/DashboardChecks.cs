using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xehn.App;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public void Advance(double seconds) => timestamp += TimeSpan.FromSeconds(seconds).Ticks;
}

internal static class DashboardChecks
{
    public static void Run(Action<bool, string> check)
    {
        var time = new ManualTimeProvider(); var series = new TrafficSeries(time);
        series.Add(0);
        check(!series.HasVisibleTraffic && TrafficChart.Project(series, 0, 300, 42).Length == 0, "all-zero history invents no traffic peaks or animation");
        void CheckBaseline(TrafficSeries? data, string name)
        {
            var idle = new TrafficChart { Width = 200, Height = 42, Series = data };
            idle.Measure(new Size(200, 42)); idle.Arrange(new Rect(0, 0, 200, 42)); idle.UpdateLayout();
            var bitmap = new RenderTargetBitmap(200, 42, 96, 96, PixelFormats.Pbgra32); bitmap.Render(idle);
            var pixels = new byte[200 * 42 * 4]; bitmap.CopyPixels(pixels, 200 * 4, 0);
            check(Enumerable.Range(0, 200).All(x => pixels[(40 * 200 + x) * 4 + 3] > 0) && Enumerable.Range(0, 200).All(x => pixels[(10 * 200 + x) * 4 + 3] == 0), name);
        }
        CheckBaseline(null, "new chart always draws a full-width horizontal zero baseline");
        CheckBaseline(series, "measured zero traffic keeps the horizontal baseline visible");
        time.Advance(2); series.Add(8192); time.Advance(2); series.Add(0);
        check(series.Snapshot().Length == 3 && series.HasVisibleTraffic, "a measured zero preserves preceding traffic instead of clearing it");
        var before = TrafficChart.Project(series, series.NowSeconds, 300, 42);
        time.Advance(0.5);
        var after = TrafficChart.Project(series, series.NowSeconds, 300, 42);
        check(before.Length == after.Length && after.Zip(before).All(p => Math.Abs(p.First.X - p.Second.X - 300 * 0.5 / 90) < 1e-6), "history moves smoothly right with elapsed time and no new samples");
        time.Advance(70);
        check(series.HasVisibleTraffic, "old traffic remains visible inside the disconnected time window");
        time.Advance(25);
        check(!series.HasVisibleTraffic && series.Snapshot().Length == 0, "history expires only after leaving the real 90-second window");
        CheckBaseline(series, "expired / disconnected traffic returns to the same continuous zero line");
        series.Add(double.NaN); time.Advance(1); series.Add(-5); time.Advance(1); series.Add(double.PositiveInfinity);
        check(series.Snapshot().All(p => p.Value == 0), "invalid and negative counters cannot produce broken geometry");

        Point[] peaks = [new(0, 38), new(20, 2), new(21, 40), new(60, 8), new(90, 38)];
        var geometry = TrafficChart.SmoothCurve(peaks);
        var path = PathGeometry.CreateFromGeometry(geometry);
        check(path.Figures.SelectMany(f => f.Segments).All(s => s is BezierSegment or PolyBezierSegment), "traffic uses cubic curves, not straight polyline segments");
        var bounds = geometry.Bounds;
        check(bounds.Top >= 1.99 && bounds.Bottom <= 40.01, "smooth interpolation never invents negative or overshooting traffic");

        // An isolated off-screen WPF window, not the client window: exercises
        // Loaded / hidden / Unloaded animation clocks without any engine work.
        var ring = new ProgressRing { Width = 32, Height = 32, IsActive = true };
        var liveTime = new ManualTimeProvider(); var liveSeries = new TrafficSeries(liveTime);
        liveSeries.Add(4096);
        var chart = new TrafficChart { Width = 200, Height = 42, Series = liveSeries };
        var fixture = new Window { Width = 240, Height = 120, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = new StackPanel { Children = { ring, chart } } };
        try
        {
            fixture.Show(); Pump();
            check(ring.IsAnimating && chart.IsAnimating, "visible progress ring and retained-history chart run animation clocks");
            ring.Visibility = Visibility.Collapsed; chart.Visibility = Visibility.Collapsed; Pump();
            check(!ring.IsAnimating && !chart.IsAnimating, "hidden UI releases animation / rendering subscriptions");
            ring.Visibility = Visibility.Visible; chart.Visibility = Visibility.Visible; Pump();
            check(ring.IsAnimating && chart.IsAnimating, "showing UI resumes animations without resetting traffic");
            Task.Run(() => liveSeries.Add(2048)).GetAwaiter().GetResult(); Pump();
            check(liveSeries.Snapshot().Last().Value == 2048 && chart.IsAnimating, "background samples safely marshal visual invalidation to the UI dispatcher");
            liveTime.Advance(100); chart.Visibility = Visibility.Collapsed; chart.Visibility = Visibility.Visible; Pump();
            check(!chart.IsAnimating && !liveSeries.HasVisibleTraffic, "expired history releases rendering hooks instead of animating an empty chart");
            ring.IsActive = false;
            check(!ring.IsAnimating, "finished connection stops its progress-ring clock");
        }
        finally { fixture.Close(); Pump(); }
        check(!ring.IsAnimating && !chart.IsAnimating, "closed UI leaves no animation / rendering subscriptions");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
}
