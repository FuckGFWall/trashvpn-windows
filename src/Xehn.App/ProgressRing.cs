using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Xehn.App;

public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ProgressRing), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, ActivityChanged));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty AngleProperty = DependencyProperty.Register("Angle", typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool IsAnimating => HasAnimatedProperties;
    public ProgressRing()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => BeginAnimation(AngleProperty, null);
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }
    private static void ActivityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ProgressRing)d).UpdateAnimation();
    private void UpdateAnimation()
    {
        if (IsActive && IsLoaded && IsVisible)
        {
            if (!IsAnimating) BeginAnimation(AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.05)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else BeginAnimation(AngleProperty, null);
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (!IsActive || ActualWidth <= StrokeThickness || ActualHeight <= StrokeThickness) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (Math.Min(ActualWidth, ActualHeight) - StrokeThickness) / 2;
        var arc = new StreamGeometry();
        using (var path = arc.Open())
        {
            path.BeginFigure(new(center.X, center.Y - radius), false, false);
            path.ArcTo(new(center.X - radius, center.Y), new(radius, radius), 0, true, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.PushTransform(new RotateTransform((double)GetValue(AngleProperty), center.X, center.Y));
        dc.DrawGeometry(null, new Pen(Stroke, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        dc.Pop();
    }
}
