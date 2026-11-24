using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Xehn.App;

internal static class PreparationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var bar = new PreparationTrack(() => true) { Width = 360, Height = 42, IsActive = true, Progress = 20 };
        var host = new Window { Width = 400, Height = 160, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = bar };
        try
        {
            host.Show(); Pump();
            check(bar.IsAnimating && bar.TrackThickness == 10 && Math.Abs(bar.RegistrationPosition - 122) < .01, "single timeline animates work and places registration at one third of its usable width");
            check(Math.Abs(bar.ReadyPosition - 354)<.01, "ready endpoint remains at the right end independently of the registration milestone");
            bar.Progress = 75;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(45) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
            check(bar.DisplayedProgress < 75, "measured progress eases toward the new value rather than jumping");
            bar.IsActive = false;
            check(!bar.IsAnimating && bar.DisplayedProgress == 75, "pausing stops both sheen and interpolation without losing measured progress");
            bar.IsActive = true; Pump(); check(bar.IsAnimating, "continuing restarts the activity animation");
            bar.Visibility = Visibility.Collapsed; Pump(); check(!bar.IsAnimating, "hidden timeline releases all animation clocks");
            bar.Visibility = Visibility.Visible; Pump(); check(bar.IsAnimating, "visible unfinished timeline resumes animation");
            bar.IsReady = true; bar.Progress = 100; bar.RegistrationComplete = true; Pump();
            check(!bar.IsAnimating && bar.FillBrush is SolidColorBrush { Color: var green } && green == Color.FromRgb(39, 148, 83), "cache-ready timeline immediately becomes solid green and stops all animation");
            // Render a detached copy so Fluent's window entrance/visibility
            // transitions do not become part of the pixel assertion.
            var rendered = new PreparationTrack { Width = 360, Height = 42, IsReady = true, Progress = 100, RegistrationComplete = true };
            rendered.Measure(new Size(360, 42)); rendered.Arrange(new Rect(0, 0, 360, 42)); rendered.UpdateLayout();
            var bitmap = new RenderTargetBitmap(360, 42, 96, 96, PixelFormats.Pbgra32); bitmap.Render(rendered);
            var pixels = new byte[360 * 42 * 4]; bitmap.CopyPixels(pixels, 360 * 4, 0);
            IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph] : drawing is DrawingGroup group ? group.Children.SelectMany(Glyphs) : [];
            var readyText=Glyphs(VisualTreeHelper.GetDrawing(rendered)).Last();
            var readyInk=readyText.Bounds;
            check(Math.Abs(readyInk.Left+readyInk.Width/2-rendered.ReadyPosition)<1.5, "ready text is centered directly under its endpoint just like registration");
            check(new[] { 20, 200, 340 }.All(x => pixels[(10 * 360 + x) * 4 + 1] > pixels[(10 * 360 + x) * 4 + 2] && pixels[(10 * 360 + x) * 4 + 3] == 255), "rendered green track spans both registration and selection, not only its last segment");
            var peer = UIElementAutomationPeer.CreatePeerForElement(bar)!;
            var range = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue);
            check(peer.GetAutomationControlType() == AutomationControlType.ProgressBar && range.IsReadOnly && range.Value == 100 && range.Minimum == 0 && range.Maximum == 100, "combined custom timeline remains an accessible read-only progress bar");
        }
        finally { host.Close(); Pump(); }
        check(!bar.IsAnimating, "unloaded preparation timeline leaves no running animations");
        var reduced = new PreparationTrack(() => false) { IsActive = true, Progress = 42 };
        check(!reduced.IsAnimating && reduced.DisplayedProgress == 42, "reduced-motion preference preserves measured progress without interpolation or sheen");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
}
