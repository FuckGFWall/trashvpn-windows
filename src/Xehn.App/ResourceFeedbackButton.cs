using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Xehn.App;

// Keep the native Button interaction and normal-label measurement. Feedback
// overlays its content without resizing it or retaining a success indefinitely.
public sealed class ResourceFeedbackButton : Button
{
    public static readonly DependencyProperty IsWorkingProperty = DependencyProperty.Register(nameof(IsWorking), typeof(bool), typeof(ResourceFeedbackButton), new PropertyMetadata(false, FeedbackChanged));
    public static readonly DependencyProperty IsSuccessfulProperty = DependencyProperty.Register(nameof(IsSuccessful), typeof(bool), typeof(ResourceFeedbackButton), new PropertyMetadata(false, FeedbackChanged));
    private static readonly DependencyPropertyKey ShowSuccessPropertyKey = DependencyProperty.RegisterReadOnly(nameof(ShowSuccess), typeof(bool), typeof(ResourceFeedbackButton), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowSuccessProperty = ShowSuccessPropertyKey.DependencyProperty;
    public bool IsWorking { get => (bool)GetValue(IsWorkingProperty); set => SetValue(IsWorkingProperty, value); }
    public bool IsSuccessful { get => (bool)GetValue(IsSuccessfulProperty); set => SetValue(IsSuccessfulProperty, value); }
    public bool ShowSuccess => (bool)GetValue(ShowSuccessProperty);
    private readonly DispatcherTimer successTimer;
    public ResourceFeedbackButton()
    {
        successTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        successTimer.Tick += (_, _) => ClearFeedback();
        Unloaded += (_, _) => ClearFeedback();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearFeedback(); };
    }
    private static void FeedbackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (ResourceFeedbackButton)d;
        button.ClearFeedback();
        if (!button.IsWorking && button.IsSuccessful)
        {
            button.SetValue(ShowSuccessPropertyKey, true);
            button.successTimer.Start();
        }
    }
    private void ClearFeedback() { successTimer?.Stop(); SetValue(ShowSuccessPropertyKey, false); }
}
