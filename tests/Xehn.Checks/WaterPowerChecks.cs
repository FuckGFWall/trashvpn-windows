using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xehn.App;

internal static class WaterPowerChecks
{
    public static void Run(Action<bool, string> check, string outDir)
    {
        var time = new ManualTimeProvider(); var seed = 100; var motion = true;
        var water = new WaterPowerSurface(time, () => ++seed, () => motion) { Width = 146, Height = 146, IdleBrush = Brushes.Gray, AccentBrush = Brushes.DarkOrange };
        var window = new Window { Content = water, Width = 160, Height = 160, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        RenderTargetBitmap Render()
        {
            water.InvalidateVisual(); water.UpdateLayout(); var local = new DrawingVisual();
            using (var dc = local.RenderOpen()) dc.DrawRectangle(new VisualBrush(water), null, new Rect(0, 0, 146, 146));
            var bitmap = new RenderTargetBitmap(146, 146, 96, 96, PixelFormats.Pbgra32); bitmap.Render(local); return bitmap;
        }
        uint Pixel(int x, int y) { var bitmap = Render(); var pixels = new byte[146 * 146 * 4]; bitmap.CopyPixels(pixels, 146 * 4, 0); return BitConverter.ToUInt32(pixels, (y * 146 + x) * 4); }
        void Snapshot(string name) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(Render())); using var file = File.Create(Path.Combine(outDir, name + ".png")); png.Save(file); }
        try
        {
            window.Show(); Pump();
            var gray = Pixel(73, 35);
            check(water.FillLevel == 0 && !water.IsAnimating && water.HaloColor == Colors.Gray && gray == Pixel(73, 120), "idle power circle and halo are gray, with no retained animation clock");
            Snapshot("power-water-idle");
            water.State = PowerButtonState.Connecting; time.Advance(2); water.RefreshFrame(); Pump();
            check(water.IsAnimating && water.FillLevel is > .4 and < .9 && Pixel(73, 35) == gray && Pixel(73, 120) != gray, "connecting water rises from the bottom while the upper circle remains gray");
            check(water.HaloColor != Colors.Gray && water.HaloColor != Colors.DarkOrange, "outer halo blends gradually from gray to orange with the water level");
            var firstShape = new[] { 30d, 60d, 100d }.Select(water.SurfaceHeightAt).ToArray(); var firstSeed = water.WaveSeed;
            Snapshot("power-water-rising");
            var level = water.FillLevel; time.Advance(30); water.RefreshFrame();
            check(water.FillLevel > level && water.FillLevel < 1, "long connections never fabricate a full success state before the engine reports connected");
            water.State = PowerButtonState.Idle; var beforeDrain = water.FillLevel; time.Advance(.25); water.RefreshFrame();
            check(water.FillLevel > 0 && water.FillLevel < beforeDrain && water.IsAnimating, "failed or canceled connection lowers the water instead of snapping to an orange success state");
            Snapshot("power-water-draining"); time.Advance(1); water.RefreshFrame();
            check(water.FillLevel == 0 && !water.IsAnimating && water.HaloColor == Colors.Gray && Pixel(73, 120) == gray, "finished failure drain restores the gray circle and halo and releases its clock");
            water.State = PowerButtonState.Connecting; time.Advance(2); water.RefreshFrame();
            var secondShape = new[] { 30d, 60d, 100d }.Select(water.SurfaceHeightAt).ToArray();
            check(firstSeed != water.WaveSeed && firstShape.Where((v, i) => Math.Abs(v - secondShape[i]) > .01).Any(), "each connection attempt selects new wave phases, speeds, amplitudes and frequencies rather than replaying one animation");
            var beforeSuccess = water.FillLevel; var successSeed = water.WaveSeed;
            water.State = PowerButtonState.Connected; Pump();
            check(water.FillLevel == beforeSuccess && water.IsAnimating && water.WaveSeed == successSeed, "success starts from the visible water level without snapping to orange or reseeding its wave");
            time.Advance(.1); water.RefreshFrame();
            check(water.FillLevel > beforeSuccess && water.FillLevel < 1 && water.IsAnimating, "success accelerates through an intermediate rising level before the circle is full");
            Snapshot("power-water-success-rising"); time.Advance(.3); water.RefreshFrame();
            check(water.FillLevel == 1 && !water.IsAnimating && water.HaloColor == Colors.DarkOrange && Pixel(73, 35) == Pixel(73, 120) && Pixel(73, 35) != gray, "connection and post-connection verification use a completely orange, stationary completed surface");
            Snapshot("power-water-connected");
            water.State = PowerButtonState.Idle; time.Advance(.1); water.RefreshFrame(); window.Hide(); Pump();
            check(!water.IsAnimating, "hiding the window releases the water clock during a drain");
            time.Advance(1); window.Show(); Pump();
            check(water.FillLevel == 0 && !water.IsAnimating, "restoring a hidden window ages out a completed drain without restarting it");
            water.State = PowerButtonState.Connecting; Pump(); window.Hide(); Pump();
            check(!water.IsAnimating, "hidden connecting windows do not retain a rendering subscription");
            window.Show(); Pump(); check(water.IsAnimating, "restoring an active connection resumes its water animation");
            water.State = PowerButtonState.Connected; motion = false; water.RefreshFrame();
            check(water.FillLevel == 1 && !water.IsAnimating, "disabling motion during success completion settles to full instead of leaving a partially filled connected button");
        }
        finally { window.Close(); Pump(); }
        check(!water.IsAnimating, "closing the power visual releases all rendering subscriptions");
        var reduced = new WaterPowerSurface(time, () => 9, () => false) { Width = 146, Height = 146 };
        var reducedWindow = new Window { Content = reduced, Width = 160, Height = 160, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try
        {
            reducedWindow.Show(); reduced.State = PowerButtonState.Connecting; Pump();
            check(reduced.FillLevel == .5 && !reduced.IsAnimating && reduced.SurfaceHeightAt(20) == reduced.SurfaceHeightAt(100), "reduced-motion mode uses a static partial fill with no moving waves");
            reduced.State = PowerButtonState.Connected; reduced.State = PowerButtonState.Idle; Pump();
            check(reduced.FillLevel == 0 && !reduced.IsAnimating, "reduced-motion completion and failure settle immediately");
        }
        finally { reducedWindow.Close(); }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
}
