using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Xehn.App;

internal static class WindowPresentationChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var (work, scale) in new[]
        {
            (new Rect(0, 0, 1366, 728), 1d),
            (new Rect(0, 0, 1280, 680), 1.5d),
            (new Rect(-1920, 40, 1920, 1040), 1.25d),
            (new Rect(1920, -900, 1280, 720), 2d)
        })
        {
            var fitted = WindowPresentation.FitBounds(work, new Size(900, 680), scale);
            var clamped = WindowPresentation.FitBounds(work, new Size(900, 680), scale, new Point(-5000, -5000));
            check(work.Contains(fitted) && work.Contains(clamped) && fitted.Top > work.Top && fitted.Bottom < work.Bottom && clamped.Top > work.Top,
                $"full native frame stays inside work area at {scale * 100:0}% DPI, including negative-coordinate monitors");
        }
        var regular = WindowPresentation.FitBounds(new Rect(0, 0, 1920, 1040), new Size(900, 680), 1);
        check(regular.Width == 900 && regular.Height == 680 && regular.Left == 510 && regular.Top == 180, "normal desktop preserves the smaller preferred size and centers it");

        // Native handle only: no MainWindow, Loaded handlers, tray, or engine.
        var fixture = new Window { Width = 900, Height = 680, MinWidth = 800, MinHeight = 600, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var handle = new WindowInteropHelper(fixture).EnsureHandle();
            foreach (var theme in new[] { "dark", "light", "dark", "light" })
            {
                App.ApplyTheme(theme);
                var background = ((SolidColorBrush)Application.Current.FindResource("PageBrush")).Color;
                var foreground = ((SolidColorBrush)Application.Current.FindResource("InkBrush")).Color;
                var applied = WindowPresentation.ApplyTitleBar(fixture, App.IsDarkTheme, background, foreground);
                var darkResult = DwmGetWindowAttribute(handle, 20, out var dark, sizeof(int));
                check(applied.DarkModeApplied && darkResult == 0 && dark == (theme == "dark" ? 1 : 0), $"native title-bar dark flag follows {theme} theme");
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                    check(applied.CaptionColorsApplied, $"Windows 11 accepts app-specific {theme} caption / text colors");
                else check(!applied.CaptionColorsApplied, "Windows 10 gracefully falls back to native light / dark caption without Windows 11-only colors");
            }
            App.ApplyTheme("auto");
            check(App.IsDarkTheme == ((int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0), "automatic title-bar theme resolves the actual system setting without modifying it");
            WindowPresentation.FitToWorkArea(fixture, center: true);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var gotArea = GetMonitorInfo(MonitorFromWindow(handle, 2), ref info);
            var gotBounds = GetWindowRect(handle, out var native);
            check(gotArea && gotBounds && native.Left >= info.Work.Left && native.Top >= info.Work.Top && native.Right <= info.Work.Right && native.Bottom <= info.Work.Bottom, "native window-frame bounds fit the current machine's actual monitor work area");
        }
        finally { fixture.Close(); App.ApplyTheme("light"); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
}
