using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;

namespace Xehn.App;

public enum PowerButtonState { Idle, Connecting, Connected }

// Visual fill is deliberately not a fabricated percentage from the engine.
// It stays below the top until connection is established; failure drains it.
public sealed class WaterPowerSurface : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(PowerButtonState), typeof(WaterPowerSurface), new FrameworkPropertyMetadata(PowerButtonState.Idle, StateChanged));
    public static readonly DependencyProperty IdleBrushProperty = DependencyProperty.Register(nameof(IdleBrush), typeof(Brush), typeof(WaterPowerSurface), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(WaterPowerSurface), new FrameworkPropertyMetadata(Brushes.DarkOrange, FrameworkPropertyMetadataOptions.AffectsRender));
    public PowerButtonState State { get => (PowerButtonState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public Brush IdleBrush { get => (Brush)GetValue(IdleBrushProperty); set => SetValue(IdleBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public double FillLevel { get; private set; }
    public bool IsAnimating => subscribed;
    public int WaveSeed { get; private set; }
    public Color HaloColor => Blend(ColorOf(IdleBrush), ColorOf(AccentBrush), FillLevel);
    private readonly TimeProvider clock;
    private readonly Func<int> seedFactory;
    private readonly Func<bool> motionEnabled;
    private readonly double[] phases = new double[3], speeds = new double[3], frequencies = new double[3], amplitudes = new double[3];
    private long transitionStart, waveStart;
    private double startLevel, targetLevel;
    private bool subscribed, preferencesSubscribed, completing;
    public WaterPowerSurface() : this(TimeProvider.System, () => RandomNumberGenerator.GetInt32(int.MaxValue)) { }
    public WaterPowerSurface(TimeProvider clock, Func<int> seedFactory, Func<bool>? motionEnabled = null)
    {
        this.clock = clock; this.seedFactory = seedFactory;
        this.motionEnabled = motionEnabled ?? (() => SystemParameters.ClientAreaAnimation);
        transitionStart = waveStart = clock.GetTimestamp(); IsHitTestVisible = false;
        Loaded += (_, _) => { if (!preferencesSubscribed) { SystemParameters.StaticPropertyChanged += PreferenceChanged; preferencesSubscribed = true; } RefreshFrame(); };
        Unloaded += (_, _) => { StopClock(); if (preferencesSubscribed) { SystemParameters.StaticPropertyChanged -= PreferenceChanged; preferencesSubscribed = false; } };
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshFrame(); else StopClock(); };
    }
    private static void StateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaterPowerSurface)d;
        view.startLevel = view.FillLevel; view.transitionStart = view.clock.GetTimestamp();
        view.completing = view.State == PowerButtonState.Connected && (PowerButtonState)e.OldValue == PowerButtonState.Connecting && view.startLevel < 1 && view.motionEnabled();
        if (view.State == PowerButtonState.Connecting)
        {
            view.WaveSeed = view.seedFactory(); var random = new Random(view.WaveSeed);
            for (var i = 0; i < 3; i++) { view.phases[i] = random.NextDouble() * Math.Tau; view.speeds[i] = .9 + random.NextDouble() * 1.6; view.frequencies[i] = .035 + random.NextDouble() * .065; view.amplitudes[i] = .8 + random.NextDouble() * 1.5; }
            view.waveStart = view.transitionStart;
            view.targetLevel = Math.Max(.9, Math.Min(.98, view.startLevel + .05));
        }
        view.RefreshFrame();
    }
    private void PreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.InvokeAsync(RefreshFrame);
    }
    public void RefreshFrame()
    {
        var elapsed = Math.Max(0, clock.GetElapsedTime(transitionStart).TotalSeconds);
        FillLevel = State switch
        {
            PowerButtonState.Connected => completing && motionEnabled() ? startLevel + (1 - startLevel) * (1 - Math.Pow(1 - Math.Clamp(elapsed / .32, 0, 1), 3)) : 1,
            PowerButtonState.Connecting => motionEnabled() ? startLevel + (targetLevel - startLevel) * (1 - Math.Exp(-elapsed / 2.2)) : .5,
            _ => motionEnabled() ? startLevel * (1 - Smooth(Math.Clamp(elapsed / .55, 0, 1))) : 0
        };
        if (State == PowerButtonState.Connected && (elapsed >= .32 || !motionEnabled())) { FillLevel = 1; completing = false; }
        if (FillLevel < .001 && State == PowerButtonState.Idle) FillLevel = 0;
        var needsClock = IsLoaded && IsVisible && motionEnabled() && (State == PowerButtonState.Connecting || State == PowerButtonState.Connected && completing || State == PowerButtonState.Idle && FillLevel > 0);
        if (needsClock && !subscribed) { CompositionTarget.Rendering += RenderFrame; subscribed = true; }
        else if (!needsClock) StopClock();
        InvalidateVisual();
    }
    private void RenderFrame(object? sender, EventArgs e) => RefreshFrame();
    private void StopClock() { if (subscribed) CompositionTarget.Rendering -= RenderFrame; subscribed = false; }
    public double SurfaceHeightAt(double x)
    {
        var time = Math.Max(0, clock.GetElapsedTime(waveStart).TotalSeconds);
        var wave = 0d;
        if (motionEnabled()) for (var i = 0; i < 3; i++) wave += amplitudes[i] * Math.Sin(x * frequencies[i] + phases[i] + time * speeds[i]);
        return 131 - FillLevel * 116 + wave * Math.Clamp(Math.Min(FillLevel, 1 - FillLevel) / .08, 0, 1);
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 146, ActualHeight / 146));
        var center = new Point(73, 73); var halo = new SolidColorBrush(HaloColor);
        dc.PushOpacity(.12); dc.DrawEllipse(halo, null, center, 72, 72); dc.Pop();
        dc.PushOpacity(.3); dc.DrawEllipse(null, new Pen(halo, 1), center, 66, 66); dc.Pop();
        dc.DrawEllipse(IdleBrush, null, center, 58, 58);
        if (FillLevel >= 1) dc.DrawEllipse(AccentBrush, null, center, 58, 58);
        else if (FillLevel > 0)
        {
            var water = new StreamGeometry();
            using (var path = water.Open())
            {
                path.BeginFigure(new Point(15, SurfaceHeightAt(15)), true, true);
                for (var x = 18d; x <= 132; x += 3) path.LineTo(new Point(x, SurfaceHeightAt(x)), true, false);
                path.LineTo(new Point(132, 132), true, false); path.LineTo(new Point(15, 132), true, false);
            }
            water.Freeze(); dc.PushClip(new EllipseGeometry(center, 58, 58)); dc.DrawGeometry(AccentBrush, null, water); dc.Pop();
        }
        dc.Pop();
    }
    private static double Smooth(double t) => t * t * (3 - 2 * t);
    private static Color ColorOf(Brush brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Gray;
    private static Color Blend(Color a, Color b, double t) => Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
