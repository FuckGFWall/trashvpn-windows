using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Xehn.App;

// Follow the outer log-body viewer, never the TextBox's disabled inner viewer.
public sealed class LogAutoScroll : IDisposable
{
    private readonly ScrollViewer viewer;
    private readonly TextBox text;
    private bool queued, disposed, upwardIntent;
    public bool IsFollowing { get; private set; } = true;
    public LogAutoScroll(ScrollViewer viewer, TextBox text)
    {
        this.viewer = viewer; this.text = text;
        text.TextChanged += TextChanged;
        viewer.ScrollChanged += ScrollChanged;
        viewer.IsVisibleChanged += VisibilityChanged;
        viewer.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(MouseWheel), true);
        QueueFollow();
    }
    public void Resume()
    {
        IsFollowing = true;
        upwardIntent = false;
        QueueFollow();
    }
    private bool AtBottom => viewer.ScrollableHeight - viewer.VerticalOffset <= 1;
    private void TextChanged(object sender, TextChangedEventArgs e) => QueueFollow();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (viewer.IsVisible) QueueFollow(); }
    private void MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Capture intent before text/layout callbacks can queue another jump.
        if (e.Delta > 0) { upwardIntent = true; IsFollowing = false; }
        else if (e.Delta < 0) { upwardIntent = false; if (AtBottom) Resume(); }
    }
    private void ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, viewer)) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            // Appends, trimming and resizing must not resume a paused reader.
            QueueFollow();
        }
        else if (e.VerticalChange < 0) { upwardIntent = false; IsFollowing = false; }
        else if (e.VerticalChange > 0) IsFollowing = !upwardIntent && AtBottom;
    }
    private void QueueFollow()
    {
        if (disposed || queued || !IsFollowing) return;
        queued = true;
        viewer.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            queued = false;
            if (disposed || !IsFollowing || !viewer.IsVisible) return;
            viewer.UpdateLayout();
            viewer.ScrollToBottom();
        }));
    }
    public void Dispose()
    {
        disposed = true;
        text.TextChanged -= TextChanged;
        viewer.ScrollChanged -= ScrollChanged;
        viewer.IsVisibleChanged -= VisibilityChanged;
        viewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(MouseWheel));
    }
}
