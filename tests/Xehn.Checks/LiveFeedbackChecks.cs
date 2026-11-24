using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xehn.App;

internal static class LiveFeedbackChecks
{
    public static void Run(Action<bool, string> check)
    {
        var dot = new StatusDot { Width = 12, Height = 12, IdleBrush = Brushes.Gray, ActiveBrush = Brushes.DarkOrange };
        var dotWindow = Fixture(dot);
        try
        {
            dotWindow.Show(); Pump(); var idle = Pixel(dot);
            dot.IsChecking = true; Pump();
            check(dot.IsAnimating, "checking node starts a status-dot animation clock");
            Wait(() => dot.PulseLevel > .85);
            check(Pixel(dot) != idle, "checking dot visibly moves from grey towards orange");
            Wait(() => dot.PulseLevel < .15);
            check(dot.IsAnimating, "checking dot returns towards grey and keeps pulsing");
            dotWindow.Hide(); Pump();
            check(!dot.IsAnimating, "hidden window releases the status-dot clock");
            dotWindow.Show(); Pump();
            check(dot.IsAnimating, "restoring a checking window resumes the status-dot pulse");
            dot.IsConnected = true; Pump();
            check(!dot.IsAnimating && Pixel(dot) != idle, "connected status is solid orange, even if a stale checking flag arrives");
            dot.IsChecking = false; dot.IsConnected = false; Pump();
            check(!dot.IsAnimating && Pixel(dot) == idle, "finished disconnected check restores solid grey");
        }
        finally { dotWindow.Close(); Pump(); }
        check(!dot.IsAnimating, "closed checking indicator retains no animation clock");

        var text = new TextBox { Width = double.NaN, Height = double.NaN, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        text.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost") };
        var viewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = text };
        using var follow = new LogAutoScroll(viewer, text);
        var logWindow = Fixture(viewer); logWindow.Width = 400; logWindow.Height = 240;
        try
        {
            logWindow.Show(); text.Text = Lines(160); Wait(() => Bottom(viewer));
            check(follow.IsFollowing && viewer.VerticalOffset > 0, "log viewer starts following the newest entries at the bottom");
            var height = viewer.ScrollableHeight; text.Text += Lines(20); Wait(() => viewer.ScrollableHeight > height && Bottom(viewer));
            check(follow.IsFollowing, "appended logs continue scrolling to the new bottom");
            // Queue a new-line follow, then express upward wheel intent before
            // that callback executes. The queued follow must not win the race.
            text.Text += Lines(10);
            viewer.UpdateLayout(); viewer.ScrollToBottom();
            text.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Wait(() => Bottom(viewer));
            check(!follow.IsFollowing, "an already-queued automatic bottom command cannot erase upward wheel intent");
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - 150); Wait(() => !Bottom(viewer));
            var offset = viewer.VerticalOffset; text.Text += Lines(20); Pump(); Pump();
            check(!follow.IsFollowing && Math.Abs(viewer.VerticalOffset - offset) < 1, "upward wheel pauses follow and new lines do not pull the reader back down");
            logWindow.Height += 30; Pump();
            check(!follow.IsFollowing, "viewport resizing cannot resume a paused log reader");
            viewer.ScrollToBottom(); Wait(() => follow.IsFollowing && Bottom(viewer));
            height = viewer.ScrollableHeight; text.Text += Lines(20); Wait(() => viewer.ScrollableHeight > height && Bottom(viewer));
            check(follow.IsFollowing, "reaching the bottom resumes live log following");
            viewer.ScrollToVerticalOffset(100); Wait(() => !follow.IsFollowing);
            follow.Resume(); Wait(() => follow.IsFollowing && Bottom(viewer));
            check(follow.IsFollowing, "fresh log-page visit resumes at the latest entries");
            logWindow.Hide(); text.Text += Lines(30); Pump(); logWindow.Show(); Wait(() => Bottom(viewer));
            check(follow.IsFollowing, "logs accumulated while hidden are followed when the page becomes visible");
            viewer.ScrollToVerticalOffset(100); Wait(() => !follow.IsFollowing);
            follow.Dispose(); offset = viewer.VerticalOffset; text.Text += Lines(5); Pump();
            check(Math.Abs(viewer.VerticalOffset - offset) < 1, "disposing log-follow behavior removes its event subscriptions and pending jumps");
        }
        finally { logWindow.Close(); Pump(); }
    }
    private static Window Fixture(UIElement content) => new() { Width = 120, Height = 100, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = content };
    private static string Lines(int count) => string.Join('\n', Enumerable.Range(0, count).Select(n => $"isolated log line {n}")) + "\n";
    private static bool Bottom(ScrollViewer viewer) => viewer.ScrollableHeight > 0 && viewer.ScrollableHeight - viewer.VerticalOffset < 1;
    private static uint Pixel(FrameworkElement element)
    {
        element.InvalidateVisual(); element.UpdateLayout();
        // Sample local bounds, not the centered child's parent layout offset.
        var local = new DrawingVisual();
        using (var dc = local.RenderOpen()) dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, 12, 12));
        var bitmap = new RenderTargetBitmap(12, 12, 96, 96, PixelFormats.Pbgra32); bitmap.Render(local);
        var data = new byte[12 * 12 * 4]; bitmap.CopyPixels(data, 48, 0);
        return BitConverter.ToUInt32(data, (6 * 12 + 6) * 4);
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private static void Wait(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        do { Pump(); if (ready()) return; Thread.Sleep(10); } while (DateTime.UtcNow < until);
        throw new TimeoutException("Live feedback fixture did not settle");
    }
}
