using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Xehn.App;

public sealed class StatusDot : FrameworkElement
{
    public static readonly DependencyProperty IsCheckingProperty = DependencyProperty.Register(nameof(IsChecking), typeof(bool), typeof(StatusDot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, StateChanged));
    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(nameof(IsConnected), typeof(bool), typeof(StatusDot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, StateChanged));
    public static readonly DependencyProperty IdleBrushProperty = DependencyProperty.Register(nameof(IdleBrush), typeof(Brush), typeof(StatusDot), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(nameof(ActiveBrush), typeof(Brush), typeof(StatusDot), new FrameworkPropertyMetadata(Brushes.DarkOrange, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PulseProperty = DependencyProperty.Register("Pulse", typeof(double), typeof(StatusDot), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsChecking { get => (bool)GetValue(IsCheckingProperty); set => SetValue(IsCheckingProperty, value); }
    public bool IsConnected { get => (bool)GetValue(IsConnectedProperty); set => SetValue(IsConnectedProperty, value); }
    public Brush IdleBrush { get => (Brush)GetValue(IdleBrushProperty); set => SetValue(IdleBrushProperty, value); }
    public Brush ActiveBrush { get => (Brush)GetValue(ActiveBrushProperty); set => SetValue(ActiveBrushProperty, value); }
    public bool IsAnimating => HasAnimatedProperties;
    public double PulseLevel => (double)GetValue(PulseProperty);
    public StatusDot()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => BeginAnimation(PulseProperty, null);
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }
    private static void StateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusDot)d).UpdateAnimation();
    private void UpdateAnimation()
    {
        if (IsChecking && !IsConnected && IsLoaded && IsVisible)
        {
            if (!IsAnimating) BeginAnimation(PulseProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else BeginAnimation(PulseProperty, null);
    }
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2;
        dc.DrawEllipse(IsConnected ? ActiveBrush : IdleBrush, null, center, radius, radius);
        if (IsChecking && !IsConnected)
        {
            dc.PushOpacity(PulseLevel);
            dc.DrawEllipse(ActiveBrush, null, center, radius, radius);
            dc.Pop();
        }
    }
}
