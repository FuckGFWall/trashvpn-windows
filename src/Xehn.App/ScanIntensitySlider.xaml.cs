using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Xehn.App;

public partial class ScanIntensitySlider : UserControl
{
    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(nameof(Intensity), typeof(string), typeof(ScanIntensitySlider), new FrameworkPropertyMetadata("medium", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, IntensityChanged));
    public string Intensity { get => (string)GetValue(IntensityProperty); set => SetValue(IntensityProperty, value); }
    private bool synchronizing, dragging;
    public ScanIntensitySlider()
    {
        InitializeComponent();
        IntensityTrack.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(DragStarted), true);
        IntensityTrack.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(DragCompleted), true);
        Loaded += (_, _) => Synchronize();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { dragging = false; Synchronize(); } };
        Unloaded += (_, _) => { dragging = false; Synchronize(); };
        Synchronize();
    }
    private static void IntensityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ScanIntensitySlider)d;
        control.dragging = false; control.Synchronize();
    }
    private void Synchronize()
    {
        if (IntensityTrack == null) return;
        synchronizing = true;
        try { IntensityTrack.Value = Intensity == "low" ? 0 : Intensity == "high" ? 2 : 1; UpdateLabels(); }
        finally { synchronizing = false; }
    }
    private void ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntensityTrack == null || synchronizing) return;
        UpdateLabels();
        if (!dragging) { if (IsEnabled) Commit(); else Synchronize(); }
    }
    private void DragStarted(object sender, DragStartedEventArgs e) => dragging = true;
    private void DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        if (e.Canceled || !IsEnabled) Synchronize(); else Commit();
    }
    private void Commit() => SetCurrentValue(IntensityProperty, (int)Math.Round(IntensityTrack.Value) switch { 0 => "low", 2 => "high", _ => "medium" });
    private void UpdateLabels()
    {
        if (LowLabel == null) return;
        var labels = new[] { LowLabel, MediumLabel, HighLabel };
        for (var i = 0; i < labels.Length; i++) labels[i].SetResourceReference(TextBlock.ForegroundProperty, i == (int)Math.Round(IntensityTrack.Value) ? "Accent" : "MutedBrush");
    }
}
