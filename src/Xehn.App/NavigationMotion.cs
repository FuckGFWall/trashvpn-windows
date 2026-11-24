using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Xehn.App;

// A single moving selection indicator, plus finite icon micro-interactions.
// No connection/model notifications enter this path or restart the animations.
public sealed class NavigationMotion : IDisposable
{
    private readonly FrameworkElement host;
    private readonly FrameworkElement indicator;
    private readonly TranslateTransform slide = new();
    private readonly Dictionary<RadioButton, IconMotion> items = new();
    private readonly Func<bool> motionEnabled;
    private bool disposed;
    private int revision;
    public double IndicatorOffset => slide.Y;
    public bool IsAnimating => slide.HasAnimatedProperties || items.Values.Any(i => i.IsAnimating);
    public int TransitionCount { get; private set; }

    public NavigationMotion(FrameworkElement host, FrameworkElement indicator, IEnumerable<RadioButton> buttons, Func<bool>? motionEnabled = null)
    {
        this.host = host; this.indicator = indicator;
        this.motionEnabled = motionEnabled ?? (() => SystemParameters.ClientAreaAnimation);
        indicator.RenderTransform = slide;
        foreach (var button in buttons)
        {
            if (button.Content is not StackPanel content || content.Children.OfType<FrameworkElement>().FirstOrDefault() is not { } glyph) continue;
            items.Add(button, new IconMotion(glyph));
            button.Checked += Checked;
        }
        host.Loaded += Loaded;
        host.Unloaded += Unloaded;
        host.SizeChanged += SizeChanged;
        host.IsVisibleChanged += VisibilityChanged;
        SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
        Synchronize(false);
    }
    private void Checked(object sender, RoutedEventArgs e) => Synchronize(true);
    private void Loaded(object sender, RoutedEventArgs e) => Synchronize(false);
    private void Unloaded(object sender, RoutedEventArgs e) => Synchronize(false);
    private void SizeChanged(object sender, SizeChangedEventArgs e) => Synchronize(false);
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Synchronize(false);
    private void SystemPreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) host.Dispatcher.InvokeAsync(() => { if (!disposed) Synchronize(false); });
    }
    private void Synchronize(bool animate)
    {
        if (disposed) return;
        var selected = items.Keys.FirstOrDefault(i => i.IsChecked == true);
        var from = slide.Y;
        Stop();
        if (selected == null) { indicator.Visibility = Visibility.Collapsed; return; }
        indicator.Visibility = Visibility.Visible;
        var target = selected.TranslatePoint(new Point(0, (selected.ActualHeight - indicator.Height) / 2), host).Y;
        slide.Y = target;
        if (!animate || !host.IsLoaded || !host.IsVisible || !motionEnabled()) return;
        TransitionCount++;
        Animate(slide, TranslateTransform.YProperty, 220, from, target);
        var icon = items[selected];
        switch ((string?)selected.Tag)
        {
            case "Settings": Animate(icon.Rotate, RotateTransform.AngleProperty, 320, 0, 35, -8, 0); break;
            case "About":
                Animate(icon.Scale, ScaleTransform.ScaleXProperty, 280, 1, .86, 1.08, 1);
                Animate(icon.Scale, ScaleTransform.ScaleYProperty, 280, 1, .86, 1.08, 1); break;
            case "Logs":
            default: Animate(icon.Translate, TranslateTransform.YProperty, 280, 0, -3, 0); break;
        }
    }
    private void Animate(Animatable target, DependencyProperty property, int milliseconds, params double[] values)
    {
        var current = revision;
        var motion = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(milliseconds), FillBehavior = FillBehavior.Stop };
        for (var i = 0; i < values.Length; i++)
            motion.KeyFrames.Add(new SplineDoubleKeyFrame(values[i], KeyTime.FromPercent((double)i / (values.Length - 1)), new KeySpline(.2, 0, 0, 1)));
        motion.Completed += (_, _) => { if (!disposed && revision == current) target.BeginAnimation(property, null); };
        target.BeginAnimation(property, motion, HandoffBehavior.SnapshotAndReplace);
    }
    private void Stop()
    {
        ++revision;
        slide.BeginAnimation(TranslateTransform.YProperty, null);
        foreach (var item in items.Values) item.Stop();
    }
    public void Dispose()
    {
        if (disposed) return;
        Stop(); disposed = true;
        foreach (var button in items.Keys) button.Checked -= Checked;
        host.Loaded -= Loaded; host.Unloaded -= Unloaded; host.SizeChanged -= SizeChanged;
        host.IsVisibleChanged -= VisibilityChanged;
        SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
    }
    private sealed class IconMotion
    {
        public ScaleTransform Scale { get; } = new();
        public RotateTransform Rotate { get; } = new();
        public TranslateTransform Translate { get; } = new();
        public bool IsAnimating => Scale.HasAnimatedProperties || Rotate.HasAnimatedProperties || Translate.HasAnimatedProperties;
        public IconMotion(FrameworkElement glyph)
        {
            var transforms = new TransformGroup(); transforms.Children.Add(Scale); transforms.Children.Add(Rotate); transforms.Children.Add(Translate);
            glyph.RenderTransformOrigin = new Point(.5, .5); glyph.RenderTransform = transforms;
        }
        public void Stop()
        {
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            Translate.BeginAnimation(TranslateTransform.XProperty, null); Translate.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }
}
