using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace Xehn.App;

// A single timeline: registration ends at 1/3, scanning occupies the rest.
// Animation interpolates measured progress, never invents completed work.
public sealed class PreparationTrack : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(PreparationTrack), new PropertyMetadata(0d, ProgressChanged));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(PreparationTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, ActivityChanged));
    public static readonly DependencyProperty IsReadyProperty = DependencyProperty.Register(nameof(IsReady), typeof(bool), typeof(PreparationTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, ActivityChanged));
    public static readonly DependencyProperty RegistrationCompleteProperty = DependencyProperty.Register(nameof(RegistrationComplete), typeof(bool), typeof(PreparationTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = BrushProperty(nameof(AccentBrush), Brushes.DarkOrange);
    public static readonly DependencyProperty TrackBrushProperty = BrushProperty(nameof(TrackBrush), Brushes.Gainsboro);
    public static readonly DependencyProperty LabelBrushProperty = BrushProperty(nameof(LabelBrush), Brushes.Gray);
    public static readonly DependencyProperty SurfaceBrushProperty = BrushProperty(nameof(SurfaceBrush), Brushes.White);
    private static DependencyProperty BrushProperty(string name, Brush brush) => DependencyProperty.Register(name, typeof(Brush), typeof(PreparationTrack), new FrameworkPropertyMetadata(brush, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DisplayProgressProperty = DependencyProperty.Register("DisplayProgress", typeof(double), typeof(PreparationTrack), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty TravelProperty = DependencyProperty.Register("Travel", typeof(double), typeof(PreparationTrack), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Brush ReadyBrush = new SolidColorBrush(Color.FromRgb(39, 148, 83));
    private readonly Func<bool> motionEnabled;
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool IsReady { get => (bool)GetValue(IsReadyProperty); set => SetValue(IsReadyProperty, value); }
    public bool RegistrationComplete { get => (bool)GetValue(RegistrationCompleteProperty); set => SetValue(RegistrationCompleteProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush SurfaceBrush { get => (Brush)GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public bool IsAnimating => HasAnimatedProperties;
    public double DisplayedProgress => (double)GetValue(DisplayProgressProperty);
    public double RegistrationPosition => 6 + Math.Max(0, ActualWidth - 12) / 3;
    public double ReadyPosition => Math.Max(6, ActualWidth - 6);
    public double TrackThickness => 10;
    public Brush FillBrush => IsReady ? ReadyBrush : AccentBrush;
    public PreparationTrack() : this(() => SystemParameters.ClientAreaAnimation) { }
    public PreparationTrack(Func<bool> motionEnabled)
    {
        this.motionEnabled = motionEnabled;
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateActivity();
        Unloaded += (_, _) => StopAnimations();
        IsVisibleChanged += (_, _) => UpdateActivity();
    }
    private static double Bounded(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    private static void ProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var track = (PreparationTrack)d;
        var value = Bounded((double)e.NewValue);
        if (track.motionEnabled() && track.IsLoaded && track.IsVisible && track.IsActive && !track.IsReady && value > track.DisplayedProgress)
            track.BeginAnimation(DisplayProgressProperty, new DoubleAnimation(track.DisplayedProgress, value, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        else track.BeginAnimation(DisplayProgressProperty, null);
        track.SetValue(DisplayProgressProperty, value);
    }
    private static void ActivityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PreparationTrack)d).UpdateActivity();
    private void StopAnimations()
    {
        BeginAnimation(TravelProperty, null); BeginAnimation(DisplayProgressProperty, null);
        SetValue(DisplayProgressProperty, Bounded(Progress));
    }
    private void UpdateActivity()
    {
        if (motionEnabled() && IsActive && !IsReady && IsLoaded && IsVisible)
        {
            if (!DependencyPropertyHelper.GetValueSource(this, TravelProperty).IsAnimated)
                BeginAnimation(TravelProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.35)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else StopAnimations();
    }
    protected override void OnRender(DrawingContext dc)
    {
        var width = Math.Max(0, ActualWidth - 12);
        if (width <= 0 || ActualHeight < 20) return;
        var track = new Rect(6, 5, width, TrackThickness);
        var clip = new RectangleGeometry(track, 5, 5);
        dc.DrawRoundedRectangle(TrackBrush, null, track, 5, 5);
        dc.PushClip(clip);
        var filled = width * (IsReady ? 1 : DisplayedProgress / 100);
        if (filled > 0) dc.DrawRoundedRectangle(FillBrush, null, new Rect(6, 5, filled, TrackThickness), 5, 5);
        if (motionEnabled() && IsActive && !IsReady)
        {
            // A soft moving sheen indicates live work even while a network
            // operation has no count yet; its extent is not a completion value.
            var extent = Math.Max(filled, width * .08);
            dc.PushClip(new RectangleGeometry(new Rect(6, 5, extent, TrackThickness)));
            var glow = new LinearGradientBrush();
            glow.GradientStops.Add(new(Color.FromArgb(0, 255, 255, 255), 0));
            glow.GradientStops.Add(new(Color.FromArgb(155, 255, 255, 255), .5));
            glow.GradientStops.Add(new(Color.FromArgb(0, 255, 255, 255), 1));
            dc.DrawRectangle(glow, null, new Rect(6 + (extent + 48) * (double)GetValue(TravelProperty) - 48, 5, 48, TrackThickness));
            dc.Pop();
        }
        dc.Pop();
        var position = RegistrationPosition;
        var complete = RegistrationComplete || IsReady;
        dc.DrawEllipse(SurfaceBrush, new Pen(complete ? FillBrush : TrackBrush, 2), new Point(position, 10), 7, 7);
        if (complete)
        {
            var check = new StreamGeometry();
            using (var g = check.Open()) { g.BeginFigure(new(position - 3, 10), false, false); g.LineTo(new(position - 1, 12), true, false); g.LineTo(new(position + 3, 8), true, false); }
            dc.DrawGeometry(null, new Pen(FillBrush, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, check);
        }
        var label = new FormattedText(Localization.Text("注册"), CultureInfo.CurrentUICulture, Localization.Current.Direction, new Typeface("Segoe UI, Microsoft YaHei UI"), 11, IsReady ? ReadyBrush : LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(position - label.Width / 2, 23));
        dc.DrawEllipse(SurfaceBrush, new Pen(IsReady ? ReadyBrush : TrackBrush, 2), new Point(ReadyPosition, 10), 5, 5);
        if (IsReady) dc.DrawEllipse(ReadyBrush, null, new Point(ReadyPosition, 10), 2.5, 2.5);
        var readyLabel = new FormattedText(Localization.Text("就绪"), CultureInfo.CurrentUICulture, Localization.Current.Direction, new Typeface("Segoe UI, Microsoft YaHei UI"), 11, IsReady ? ReadyBrush : LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(readyLabel, new Point(ReadyPosition - readyLabel.Width / 2, 23));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new PreparationTrackPeer(this);
    private sealed class PreparationTrackPeer(PreparationTrack owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(PreparationTrack);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => true;
        public double Minimum => 0; public double Maximum => 100; public double SmallChange => double.NaN; public double LargeChange => double.NaN;
        public double Value => owner.IsReady ? 100 : Bounded(owner.Progress);
        public void SetValue(double value) => throw new InvalidOperationException("进度条为只读。");
    }
}
