using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Xehn.App;

public sealed class ModeSelectionEventArgs(bool second) : EventArgs { public bool Second { get; } = second; }

public partial class ModeSwitch : UserControl
{
    public static readonly DependencyProperty FirstTextProperty = DependencyProperty.Register(nameof(FirstText), typeof(string), typeof(ModeSwitch), new PropertyMetadata(""));
    public static readonly DependencyProperty SecondTextProperty = DependencyProperty.Register(nameof(SecondText), typeof(string), typeof(ModeSwitch), new PropertyMetadata(""));
    public static readonly DependencyProperty IsSecondSelectedProperty = DependencyProperty.Register(nameof(IsSecondSelected), typeof(bool), typeof(ModeSwitch), new PropertyMetadata(false, SelectionChanged));
    public string FirstText { get => (string)GetValue(FirstTextProperty); set => SetValue(FirstTextProperty, value); }
    public string SecondText { get => (string)GetValue(SecondTextProperty); set => SetValue(SecondTextProperty, value); }
    public bool IsSecondSelected { get => (bool)GetValue(IsSecondSelectedProperty); set => SetValue(IsSecondSelectedProperty, value); }
    public event EventHandler<ModeSelectionEventArgs>? SelectionRequested;
    public double SelectionOffset => Slide.X;
    public bool IsAnimating => Slide.HasAnimatedProperties;
    public int TransitionCount { get; private set; }
    private bool synchronizing;
    private int revision;
    public ModeSwitch()
    {
        InitializeComponent();
        Loaded += (_, _) => Synchronize(false);
        Unloaded += (_, _) => Synchronize(false);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Synchronize(false); };
        Synchronize(false);
    }
    private static void SelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ModeSwitch)d).Synchronize(true);
    private void TrackSizeChanged(object sender, SizeChangedEventArgs e) => Synchronize(false);
    private void Synchronize(bool animate)
    {
        if (FirstChoice == null || SecondChoice == null || SelectionThumb == null) return;
        synchronizing = true;
        try { FirstChoice.IsChecked = !IsSecondSelected; SecondChoice.IsChecked = IsSecondSelected; }
        finally { synchronizing = false; }
        SelectionThumb.Width = Track.ActualWidth / 2;
        var target = IsSecondSelected ? SelectionThumb.Width : 0;
        var from = Slide.X;
        var current = ++revision;
        Slide.BeginAnimation(TranslateTransform.XProperty, null); Slide.X = target;
        if (!animate || !IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation || Math.Abs(from - target) < .1) return;
        TransitionCount++;
        var motion = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
        motion.Completed += (_, _) => { if (revision == current) { Slide.BeginAnimation(TranslateTransform.XProperty, null); Slide.X = target; } };
        Slide.BeginAnimation(TranslateTransform.XProperty, motion, HandoffBehavior.SnapshotAndReplace);
    }
    private void ChoiceChecked(object sender, RoutedEventArgs e)
    {
        if (synchronizing || !IsLoaded || !IsEnabled) return;
        var requested = ReferenceEquals(sender, SecondChoice);
        // Selection represents the confirmed mode, not RadioButton's transient
        // click state. Pending / rejected engine operations must not flash the
        // thumb to a new mode and immediately back again on PropertyChanged("").
        synchronizing = true;
        try { FirstChoice.IsChecked = !IsSecondSelected; SecondChoice.IsChecked = IsSecondSelected; }
        finally { synchronizing = false; }
        if (requested != IsSecondSelected) SelectionRequested?.Invoke(this, new ModeSelectionEventArgs(requested));
    }
}
