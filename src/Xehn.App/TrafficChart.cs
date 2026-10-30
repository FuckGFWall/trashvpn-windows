using System.Windows;
using System.Windows.Media;

namespace Xehn.App;
public sealed class TrafficChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(TrafficChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(TrafficChart), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        if (Values is not { Length: > 1 } values || values.Max() <= 0) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var scale = Math.Max(1024, values.Max());
            for (var i = 0; i < values.Length; i++)
            {
                var point = new Point(i * ActualWidth / Math.Max(44, values.Length - 1), ActualHeight - 3 - values[i] / scale * (ActualHeight - 6));
                if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
            }
        }
        dc.DrawGeometry(null, new Pen(Stroke, 2), geometry);
    }
}
