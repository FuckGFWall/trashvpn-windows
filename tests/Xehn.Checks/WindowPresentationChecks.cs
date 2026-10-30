using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Xehn.App;

#pragma warning disable WPF0001 // Match the app's .NET 10 Fluent ThemeMode opt-in.

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
        var fixture = new Window { Width = 800, Height = 680, MinWidth = 800, MaxWidth = 800, MinHeight = 600, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var handle = new WindowInteropHelper(fixture).EnsureHandle();
            WindowPresentation.DisableMaximize(fixture);
            var style = GetWindowLong(handle, -16);
            check((style & 0x10000) == 0 && (style & 0x20000) != 0 && (style & 0x40000) != 0, "native maximize is disabled while minimize and vertical resizing remain available");
            check((GetMenuState(GetSystemMenu(handle, false), 0xF030, 0) & 3) != 0, "native system-menu maximize entry is greyed out");
            SendMessage(handle, 0x112, 0xF030, 0);
            check(!IsZoomed(handle) && fixture.WindowState == WindowState.Normal, "native maximize command cannot bypass fixed-width window policy");
            // Show only this engine-free, off-screen fixture to cover WPF's
            // initial show / theme lifecycle without displaying a user client.
            fixture.Left = fixture.Top = -20000; fixture.Show();
            fixture.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            check((GetWindowLong(handle, -16) & 0x10000) == 0, "WPF initial show keeps the native maximize button disabled");
            fixture.Hide();
            var activations = new List<nint>();
            nint ObserveFrame(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
            {
                if (message == 0x86) activations.Add(wParam);
                return 0;
            }
            var source = HwndSource.FromHwnd(handle);
            source.AddHook(ObserveFrame);
            foreach (var theme in new[] { "dark", "light", "dark", "light" })
            {
                App.ApplyTheme(theme);
                var background = ((SolidColorBrush)Application.Current.FindResource("PageBrush")).Color;
                var foreground = ((SolidColorBrush)Application.Current.FindResource("InkBrush")).Color;
                var focus = GetForegroundWindow();
                GetWindowRect(handle, out var bounds);
                activations.Clear();
                var applied = WindowPresentation.ApplyTitleBar(fixture, App.IsDarkTheme, background, foreground);
                fixture.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var darkResult = DwmGetWindowAttribute(handle, 20, out var dark, sizeof(int));
                check(Application.Current.ThemeMode == (theme == "dark" ? ThemeMode.Dark : ThemeMode.Light), "WPF Fluent theme state matches the requested caption theme after dispatcher work");
                check(applied.DarkModeApplied && darkResult == 0 && dark == (theme == "dark" ? 1 : 0), $"native title-bar dark flag follows {theme} theme");
                GetWindowRect(handle, out var after);
                check(activations.Count >= 2 && activations[^1] == (fixture.IsActive ? 1 : 0) && focus == GetForegroundWindow() && bounds.Equals(after) && fixture.WindowState == WindowState.Normal,
                    "caption repaint restores actual activation state without stealing focus, moving or resizing");
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                    check(applied.CaptionColorsApplied, $"Windows 11 accepts app-specific {theme} caption / text colors");
                else check(!applied.CaptionColorsApplied, "Windows 10 gracefully falls back to native light / dark caption without Windows 11-only colors");
            }
            source.RemoveHook(ObserveFrame);
            App.ApplyTheme("auto");
            check(App.IsDarkTheme == ((int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0), "automatic title-bar theme resolves the actual system setting without modifying it");
            WindowPresentation.FitToWorkArea(fixture, center: true, fixedWidth: 800);
            check(fixture.MinWidth == fixture.Width && fixture.MaxWidth == fixture.Width && fixture.Width <= 800, "work-area fitting locks horizontal minimum and maximum to the same width");
            check((GetWindowLong(handle, -16) & 0x10000) == 0, "theme changes and work-area fitting cannot re-enable maximize");
            var constraints = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMinMax>());
            try
            {
                // Windows normally seeds positive default maximum tracking
                // limits before calling WndProc; a zero-filled struct would
                // incorrectly force WPF's min(existingMax, MaxWidth) to zero.
                Marshal.StructureToPtr(new NativeMinMax { MaxTrack = new NativePoint { X = 32767, Y = 32767 } }, constraints, false);
                SendMessage(handle, 0x24, 0, constraints); // WM_GETMINMAXINFO
                var limits = Marshal.PtrToStructure<NativeMinMax>(constraints);
                check(limits.MinTrack.X > 0 && limits.MinTrack.X == limits.MaxTrack.X && limits.MinTrack.Y < limits.MaxTrack.Y, "native resize constraints lock width but leave a vertical size range");
            }
            finally { Marshal.FreeHGlobal(constraints); }
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var gotArea = GetMonitorInfo(MonitorFromWindow(handle, 2), ref info);
            var gotBounds = GetWindowRect(handle, out var native);
            check(gotArea && gotBounds && native.Left >= info.Work.Left && native.Top >= info.Work.Top && native.Right <= info.Work.Right && native.Bottom <= info.Work.Bottom, "native window-frame bounds fit the current machine's actual monitor work area");
            var previousHeight = fixture.Height; var previousWidth = fixture.Width;
            fixture.Height -= 20;
            WindowPresentation.FitToWorkArea(fixture, center: false, fixedWidth: 800);
            check(fixture.Width == previousWidth && Math.Abs(fixture.Height - previousHeight + 20) < .1, "fixed horizontal width still permits vertical size changes");
        }
        finally { fixture.Close(); App.ApplyTheme("light"); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMinMax { public NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetSystemMenu(nint window, [MarshalAs(UnmanagedType.Bool)] bool revert);
    [DllImport("user32.dll")] private static extern uint GetMenuState(nint menu, uint item, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
