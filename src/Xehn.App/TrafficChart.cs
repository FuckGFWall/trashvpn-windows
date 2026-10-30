using System.Windows;
using System.Windows.Media;

namespace Xehn.App;
public sealed class TrafficChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(TrafficSeries), typeof(TrafficChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, SeriesChanged));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public TrafficSeries? Series { get => (TrafficSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    private bool rendering;
    public bool IsAnimating => rendering;
    private TimeSpan lastFrame;
    public TrafficChart()
    {
        Loaded += (_, _) => UpdateRendering();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateRendering();
    }
    private static void SeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrafficChart)d;
        if (e.OldValue is TrafficSeries old) WeakEventManager<TrafficSeries, EventArgs>.RemoveHandler(old, nameof(TrafficSeries.Changed), chart.SampleChanged);
        if (e.NewValue is TrafficSeries next) WeakEventManager<TrafficSeries, EventArgs>.AddHandler(next, nameof(TrafficSeries.Changed), chart.SampleChanged);
        chart.UpdateRendering();
    }
    private void SampleChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => SampleChanged(sender, e)); return; }
        InvalidateVisual(); UpdateRendering();
    }
    private void UpdateRendering()
    {
        if (IsLoaded && IsVisible && Series?.HasVisibleTraffic == true)
        {
            if (rendering) return;
            rendering = true; lastFrame = TimeSpan.MinValue;
            CompositionTarget.Rendering += Animate;
        }
        else StopRendering();
    }
    private void StopRendering() { if (rendering) { CompositionTarget.Rendering -= Animate; rendering = false; } }
    private void Animate(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (lastFrame != TimeSpan.MinValue && frame.RenderingTime - lastFrame < TimeSpan.FromSeconds(1d / 30)) return;
            lastFrame = frame.RenderingTime;
        }
        InvalidateVisual();
        UpdateRendering();
    }
    // Newest enters at the left, older measurements travel right even without
    // new VPN samples. A monotonic clock also survives wall-clock adjustments.
    public static Point[] Project(TrafficSeries series, double now, double width, double height)
    {
        var values = series.Snapshot(now);
        if (width <= 0 || height <= 4 || values.Length == 0 || values.All(p => p.Value <= 0)) return [];
        var scale = Math.Max(1024, values.Max(p => p.Value));
        return values.Reverse().Select(p => new Point((now - p.Seconds) / TrafficSeries.WindowSeconds * width, height - 2 - p.Value / scale * (height - 4))).ToArray();
    }
    // Shape-preserving cubic Hermite interpolation: shared tangents, flat
    // extrema, and no invented negative / overshooting traffic spikes.
    public static StreamGeometry SmoothCurve(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        if (points.Count == 0) return geometry;
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            var slope = new double[Math.Max(0, points.Count - 1)];
            var tangent = new double[points.Count];
            for (var i = 0; i < slope.Length; i++) slope[i] = (points[i + 1].Y - points[i].Y) / Math.Max(1e-9, points[i + 1].X - points[i].X);
            if (slope.Length > 0) { tangent[0] = slope[0]; tangent[^1] = slope[^1]; }
            for (var i = 1; i < tangent.Length - 1; i++) tangent[i] = slope[i - 1] * slope[i] <= 0 ? 0 : (slope[i - 1] + slope[i]) / 2;
            for (var i = 0; i < slope.Length; i++)
            {
                if (slope[i] == 0) { tangent[i] = tangent[i + 1] = 0; continue; }
                var a = tangent[i] / slope[i]; var b = tangent[i + 1] / slope[i];
                var length = Math.Sqrt(a * a + b * b);
                if (length > 3) { tangent[i] = 3 * a / length * slope[i]; tangent[i + 1] = 3 * b / length * slope[i]; }
            }
            for (var i = 0; i < slope.Length; i++)
            {
                var step = (points[i + 1].X - points[i].X) / 3;
                context.BezierTo(new(points[i].X + step, points[i].Y + tangent[i] * step), new(points[i + 1].X - step, points[i + 1].Y - tangent[i + 1] * step), points[i + 1], true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 4) return;
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        // Keep the zero level visible even before the first sample or after
        // the last retained sample has left the time window.
        dc.DrawLine(new Pen(Stroke, 1.2), new Point(0, ActualHeight - 2), new Point(ActualWidth, ActualHeight - 2));
        var points = Series == null ? [] : Project(Series, Series.NowSeconds, ActualWidth, ActualHeight);
        if (points.Length == 1) dc.DrawEllipse(Stroke, null, points[0], 1, 1);
        else if (points.Length > 1) dc.DrawGeometry(null, new Pen(Stroke, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, SmoothCurve(points));
        dc.Pop();
    }
}
